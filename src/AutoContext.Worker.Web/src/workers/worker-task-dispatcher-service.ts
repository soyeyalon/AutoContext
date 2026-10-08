import type { Socket } from 'node:net';
import { LengthPrefixedFrameCodec, PipeListener, type BoundPipeListener } from 'autocontext-nodejs-core';
import type { McpTask } from '#types/mcp-task.js';
import type { WorkerHostOptions } from '#types/worker-host-options.js';
import { CorrelationScope } from '../logging/correlation-scope.js';
import type { LoggerFacade } from 'autocontext-nodejs-core';

/**
 * The worker's task runner. Listens on a named pipe for task requests
 * sent by the MCP server, looks the task up by name in the worker's
 * registered {@link McpTask} set, executes it, and sends the result
 * back on the same connection. TypeScript counterpart of the C#
 * `WorkerTaskDispatcherService` in `AutoContext.Framework`.
 *
 * Each pipe connection carries exactly one task call: read one request
 * envelope, run one task, write one response envelope, close. The MCP
 * server is responsible for fanning a multi-task tool invocation out
 * across multiple concurrent connections and aggregating the results;
 * the worker only sees individual task calls.
 *
 * Wire protocol (see `docs/architecture.md` § "Protocol & Contracts"):
 * 4-byte little-endian length prefix + UTF-8 JSON payload.
 *
 * Request:  `{ "mcpTask", "data", "editorconfig" }`
 * Response: `{ "mcpTask", "status", "output", "error" }`
 *
 * Any `editorconfig` object on the request envelope is flattened into
 * `data` as properties prefixed with `editorconfig.` before the task
 * is invoked, so tasks see a single payload.
 */
export class WorkerTaskDispatcherService {
    private readonly options: WorkerHostOptions;
    private readonly tasks: ReadonlyMap<string, McpTask>;
    private readonly logger: LoggerFacade;
    private readonly stopController = new AbortController();
    private bound: BoundPipeListener | undefined;
    private runPromise: Promise<void> | undefined;
    private stopPromise: Promise<void> | undefined;

    constructor(options: WorkerHostOptions, tasks: readonly McpTask[], logger: LoggerFacade) {
        if (options.pipe.trim() === '') {
            throw new Error('Missing required configuration: pipe');
        }
        if (options.readyMarker.trim() === '') {
            throw new Error('Missing required configuration: readyMarker');
        }

        const map = new Map<string, McpTask>();
        for (const task of tasks) {
            if (map.has(task.taskName)) {
                throw new Error(
                    `Duplicate McpTask registration for task name '${task.taskName}'.`,
                );
            }
            map.set(task.taskName, task);
        }

        this.options = options;
        this.tasks = map;
        this.logger = logger;
    }

    /**
     * Starts the pipe server. Resolves once the listener is active and
     * the ready marker has been written to stderr. The returned promise
     * only rejects on immediate bind failure; subsequent per-connection
     * failures are handled in-band as error envelopes.
     *
     * When {@link signal} is aborted the server stops accepting new
     * connections, in-flight handlers are awaited, and the method
     * resolves cleanly.
     */
    async start(signal: AbortSignal): Promise<void> {
        if (this.bound !== undefined) {
            throw new Error('WorkerTaskDispatcherService has already been started.');
        }

        const listener = new PipeListener(this.options.pipe, this.logger);

        this.logger.info(`Worker listening on pipe: ${this.options.pipe}`);
        this.bound = await listener.bind();

        // Host-handshake contract: the parent (extension) process
        // scrapes worker stderr for this exact marker to know the
        // named pipe is listening. Logger output is routed elsewhere
        // and would miss the contract — this call MUST stay on
        // process.stderr.
        process.stderr.write(this.options.readyMarker + '\n');
        this.logger.info(`Worker ready marker emitted on pipe: ${this.options.pipe}`);

        if (signal.aborted) {
            await this.stop();
            return;
        }

        // Run the accept loop in the background. start() resolves now
        // so the host can return from its setup phase.
        const runSignal = AbortSignal.any([signal, this.stopController.signal]);
        this.runPromise = this.bound.run((socket, sig) => this.handleConnection(socket, sig), runSignal);

        signal.addEventListener('abort', () => void this.stop(), { once: true });
    }

    /**
     * Stops accepting new connections and awaits any in-flight
     * handlers. Safe to call multiple times; concurrent callers share
     * the same completion promise.
     */
    async stop(): Promise<void> {
        if (this.stopPromise !== undefined) {
            return this.stopPromise;
        }
        this.stopPromise = this.stopCore();
        return this.stopPromise;
    }

    private async stopCore(): Promise<void> {
        if (!this.stopController.signal.aborted) {
            this.stopController.abort();
        }
        if (this.runPromise !== undefined) {
            await this.runPromise.catch(() => undefined);
        }
        if (this.bound !== undefined) {
            await this.bound.dispose();
            this.bound = undefined;
        }
    }

    private async handleConnection(socket: Socket, signal: AbortSignal): Promise<void> {
        try {
            const channel = new LengthPrefixedFrameCodec(socket);
            const requestBytes = await channel.read(signal);
            if (requestBytes === null) {
                return;
            }

            const responseBytes = await this.dispatch(requestBytes, signal);
            await channel.write(responseBytes, signal);
        } catch (ex) {
            // `dispatch()` converts task/parse failures into structured
            // error envelopes, so the only errors that reach this catch
            // are channel-level: peer disconnect, signal abort, or the
            // pipe being torn down mid-transfer (all expected), or
            // protocol corruption / unexpected stream failures (real
            // bugs). Surface the latter through the logger instead of
            // swallowing them.
            if (!WorkerTaskDispatcherService.isExpectedConnectionError(ex)) {
                const { name, message } = WorkerTaskDispatcherService.describeError(ex);
                this.logger.error(`Unexpected error in connection handler: ${name}: ${message}`, ex);
            }
        }
    }

    private async dispatch(requestBytes: Buffer, signal: AbortSignal): Promise<Buffer> {
        // Parse the envelope first under its own narrow handler. A
        // malformed envelope has no correlation id available, so it
        // can never be scope-correlated — but neither does it execute
        // any user task, so there's nothing to mis-attribute.
        let request: unknown;
        try {
            request = JSON.parse(requestBytes.toString('utf8'));
        } catch (ex) {
            const message = ex instanceof Error ? ex.message : String(ex);
            return WorkerTaskDispatcherService.buildErrorResponse('', `Malformed request JSON: ${message}`);
        }

        if (!WorkerTaskDispatcherService.isObject(request)) {
            return WorkerTaskDispatcherService.buildErrorResponse('', 'Request must be a JSON object.');
        }

        const rawTaskName = request['mcpTask'];
        if (typeof rawTaskName !== 'string' || rawTaskName.length === 0) {
            return WorkerTaskDispatcherService.buildErrorResponse('', "Request is missing required field 'mcpTask'.");
        }
        const taskName = rawTaskName;
        const correlationId = WorkerTaskDispatcherService.readCorrelationId(request);

        // Run the dispatch — including the catch-all — inside the
        // CorrelationScope so every Logger call made by the
        // task and the failure handler is stamped with the same id
        // before reaching the LogServer pipe.
        const run = async (): Promise<Buffer> => {
            try {
                const task = this.tasks.get(taskName);
                if (task === undefined) {
                    return WorkerTaskDispatcherService.buildErrorResponse(taskName, `Unknown task '${taskName}'.`);
                }
                const data = WorkerTaskDispatcherService.buildTaskData(request);
                const output = await task.execute(data, signal);
                return WorkerTaskDispatcherService.buildSuccessResponse(taskName, output);
            } catch (ex) {
                const { name, message } = WorkerTaskDispatcherService.describeError(ex);
                this.logger.error(`Task '${taskName}' failed: ${name}: ${message}`, ex);
                return WorkerTaskDispatcherService.buildErrorResponse(taskName, `Task threw ${name}: ${message}`);
            }
        };

        return correlationId === undefined
            ? run()
            : CorrelationScope.run(correlationId, run);
    }

    private static readCorrelationId(request: Record<string, unknown>): string | undefined {
        const value = request['correlationId'];
        return typeof value === 'string' && value.length > 0 ? value : undefined;
    }

    private static describeError(ex: unknown): { name: string; message: string } {
        if (ex instanceof Error) {
            return { name: ex.name || 'Error', message: ex.message || String(ex) };
        }
        return { name: 'Error', message: ex === null ? 'null' : String(ex) };
    }

    /**
     * Returns true for the small set of errors that are an expected part
     * of normal pipe-server lifecycle: the abort signal firing, the peer
     * closing the connection, or the pipe being torn down mid-write.
     * Anything else is treated as a real bug and surfaced.
     */
    private static isExpectedConnectionError(ex: unknown): boolean {
        if (!(ex instanceof Error)) {
            return false;
        }
        if (ex.name === 'AbortError') {
            return true;
        }
        const code = (ex as NodeJS.ErrnoException).code;
        return code === 'ECONNRESET'
            || code === 'EPIPE'
            || code === 'ERR_STREAM_PREMATURE_CLOSE'
            || code === 'ERR_STREAM_DESTROYED';
    }

    private static buildTaskData(request: Record<string, unknown>): Record<string, unknown> {
        const data = WorkerTaskDispatcherService.isObject(request['data']) ? { ...request['data'] } : {};
        const ec = request['editorconfig'];

        if (WorkerTaskDispatcherService.isObject(ec)) {
            for (const [key, value] of Object.entries(ec)) {
                if (typeof value === 'string') {
                    data[`editorconfig.${key}`] = value;
                }
            }
        }

        // The rules the workspace switched off travel to the task unchanged.
        if (Array.isArray(request['disabledRules'])) {
            data['disabledRules'] = request['disabledRules'];
        }

        return data;
    }

    private static buildSuccessResponse(taskName: string, output: unknown): Buffer {
        // `JSON.stringify` throws on `BigInt`, circular references, and a
        // few other shapes. Without this guard those would propagate up to
        // the dispatcher's catch and be reported as `Task threw …`, which
        // is misleading — the task succeeded; only the response could not
        // be serialized. Synthesize a clear error envelope instead so the
        // client sees the real cause.
        let payload: string;
        try {
            payload = JSON.stringify({
                mcpTask: taskName,
                status: 'ok',
                output: output ?? null,
                error: '',
            });
        } catch (ex) {
            const { name, message } = WorkerTaskDispatcherService.describeError(ex);
            return WorkerTaskDispatcherService.buildErrorResponse(
                taskName,
                `Task output is not JSON-serializable: ${name}: ${message}`,
            );
        }
        return Buffer.from(payload, 'utf8');
    }

    private static buildErrorResponse(taskName: string, error: string): Buffer {
        return Buffer.from(
            JSON.stringify({
                mcpTask: taskName,
                status: 'error',
                output: null,
                error,
            }),
            'utf8',
        );
    }

    private static isObject(value: unknown): value is Record<string, unknown> {
        return typeof value === 'object' && value !== null && !Array.isArray(value);
    }
}
