import { describe, it, expect, afterEach, onTestFinished } from 'vitest';
import * as net from 'node:net';
import * as fs from 'node:fs';
import { randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import type { McpTask } from '#types/mcp-task.js';
import { WorkerTaskDispatcherService } from '#src/workers/worker-task-dispatcher-service.js';
import { LengthPrefixedFrameCodec, NullLogger } from 'autocontext-nodejs-core';
import { CorrelationScope } from '#src/logging/correlation-scope.js';
import type { LoggerFacade } from 'autocontext-nodejs-core';

function makePipeName(): string {
    const id = randomUUID();
    if (process.platform === 'win32') {
        return `\\\\.\\pipe\\autocontext-test-${id}`;
    }
    return join(tmpdir(), `autocontext-test-${id}.sock`);
}

async function sendRequest(pipe: string, request: unknown): Promise<Record<string, unknown>> {
    const socket = net.createConnection(pipe);
    await new Promise<void>((resolve, reject) => {
        socket.once('connect', resolve);
        socket.once('error', reject);
    });

    const payload = typeof request === 'string'
        ? Buffer.from(request, 'utf8')
        : Buffer.from(JSON.stringify(request), 'utf8');
    const channel = new LengthPrefixedFrameCodec(socket);
    await channel.write(payload);

    const response = await channel.read();
    socket.end();
    socket.destroy();

    if (response === null) {
        throw new Error('Server closed without sending a response.');
    }
    return JSON.parse(response.toString('utf8')) as Record<string, unknown>;
}

class EchoTask implements McpTask {
    readonly taskName = 'echo';
    async execute(data: Record<string, unknown>): Promise<unknown> {
        return { echoed: data };
    }
}

class ThrowingTask implements McpTask {
    readonly taskName = 'throws';
    async execute(): Promise<unknown> {
        throw new Error('boom');
    }
}

class HangingTask implements McpTask {
    readonly taskName = 'hang';
    async execute(_data: Record<string, unknown>, signal: AbortSignal): Promise<unknown> {
        return new Promise((_resolve, reject) => {
            signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
        });
    }
}

class NullThrowingTask implements McpTask {
    readonly taskName = 'null-throw';
    async execute(): Promise<unknown> {
        // eslint-disable-next-line @typescript-eslint/no-throw-literal
        throw null;
    }
}

class BigIntTask implements McpTask {
    readonly taskName = 'bigint';
    async execute(): Promise<unknown> {
        // BigInt cannot be serialized by JSON.stringify — exercises the
        // guarded-stringify path in buildSuccessResponse.
        return { big: 1n };
    }
}

describe('WorkerTaskDispatcherService', () => {
    const services: WorkerTaskDispatcherService[] = [];
    const controllers: AbortController[] = [];

    afterEach(async () => {
        for (const c of controllers) {
            c.abort();
        }
        for (const s of services) {
            await s.stop();
        }
        services.length = 0;
        controllers.length = 0;
    });

    async function startService(tasks: McpTask[]): Promise<{ pipe: string; service: WorkerTaskDispatcherService }> {
        const pipe = makePipeName();
        const controller = new AbortController();
        const service = new WorkerTaskDispatcherService(
            { pipe, readyMarker: '[test] Ready.' },
            tasks,
            NullLogger.instance,
        );
        services.push(service);
        controllers.push(controller);
        await service.start(controller.signal);
        return { pipe, service };
    }

    it('throws when the pipe option is blank', () => {
        expect(() => new WorkerTaskDispatcherService({ pipe: '   ', readyMarker: 'x' }, [], NullLogger.instance)).toThrow(
            /pipe/,
        );
    });

    it('throws when the readyMarker option is blank', () => {
        expect(() => new WorkerTaskDispatcherService({ pipe: 'p', readyMarker: '' }, [], NullLogger.instance)).toThrow(
            /readyMarker/,
        );
    });

    it('throws when two tasks register the same name', () => {
        expect(
            () =>
                new WorkerTaskDispatcherService(
                    { pipe: 'p', readyMarker: 'x' },
                    [new EchoTask(), new EchoTask()],
                    NullLogger.instance,
                ),
        ).toThrow(/Duplicate McpTask/);
    });

    it('dispatches a request to the matching task and returns its output', async () => {
        const { pipe } = await startService([new EchoTask()]);

        const response = await sendRequest(pipe, {
            mcpTask: 'echo',
            data: { hello: 'world' },
        });

        expect(response['status']).toBe('ok');
        expect(response['mcpTask']).toBe('echo');
        expect(response['output']).toEqual({ echoed: { hello: 'world' } });
        expect(response['error']).toBe('');
    });

    it('flattens editorconfig into data before invoking the task', async () => {
        const { pipe } = await startService([new EchoTask()]);

        const response = await sendRequest(pipe, {
            mcpTask: 'echo',
            data: { hello: 'world' },
            editorconfig: { indent_style: 'space', indent_size: '4' },
        });

        expect(response['status']).toBe('ok');
        expect(response['output']).toEqual({
            echoed: {
                hello: 'world',
                'editorconfig.indent_style': 'space',
                'editorconfig.indent_size': '4',
            },
        });
    });

    it('hands the disabled rules to the task', async () => {
        const { pipe } = await startService([new EchoTask()]);

        const response = await sendRequest(pipe, {
            mcpTask: 'echo',
            data: { hello: 'world' },
            disabledRules: ['lang-typescript#INST0018', 'lang-javascript'],
        });

        expect(response['status']).toBe('ok');
        expect(response['output']).toEqual({
            echoed: {
                hello: 'world',
                disabledRules: ['lang-typescript#INST0018', 'lang-javascript'],
            },
        });
    });

    it('returns an error envelope for an unknown task name', async () => {
        const { pipe } = await startService([new EchoTask()]);

        const response = await sendRequest(pipe, { mcpTask: 'nope', data: {} });

        expect(response['status']).toBe('error');
        expect(response['mcpTask']).toBe('nope');
        expect(response['error']).toMatch(/Unknown task/);
    });

    it('returns an error envelope when mcpTask is missing', async () => {
        const { pipe } = await startService([new EchoTask()]);

        const response = await sendRequest(pipe, { data: {} });

        expect(response['status']).toBe('error');
        expect(response['error']).toMatch(/mcpTask/);
    });

    it('returns an error envelope when the task throws', async () => {
        const { pipe } = await startService([new ThrowingTask()]);

        const response = await sendRequest(pipe, { mcpTask: 'throws', data: {} });

        expect(response['status']).toBe('error');
        expect(response['mcpTask']).toBe('throws');
        expect(response['error']).toMatch(/boom/);
    });

    it('returns an error envelope on malformed request JSON', async () => {
        const { pipe } = await startService([new EchoTask()]);

        const response = await sendRequest(pipe, '{not json');

        expect(response['status']).toBe('error');
        expect(response['error']).toMatch(/Malformed request JSON/);
    });

    it('handles multiple concurrent connections independently', async () => {
        const { pipe } = await startService([new EchoTask()]);
        const indices = [0, 1, 2, 3, 4];

        const responses = await Promise.all(
            indices.map((i) => sendRequest(pipe, { mcpTask: 'echo', data: { i } })),
        );

        const summary = responses.map((r) => ({ status: r['status'], output: r['output'] }));
        const expected = indices.map((i) => ({ status: 'ok', output: { echoed: { i } } }));
        expect(summary).toEqual(expected);
    });

    it('returns a sensible error envelope when a task throws a non-Error value', async () => {
        const { pipe } = await startService([new NullThrowingTask()]);

        const response = await sendRequest(pipe, { mcpTask: 'null-throw', data: {} });

        expect(response['status']).toBe('error');
        expect(response['mcpTask']).toBe('null-throw');
        expect(response['error']).toMatch(/Task threw Error: null/);
    });

    it('returns an error envelope when task output is not JSON-serializable', async () => {
        const { pipe } = await startService([new BigIntTask()]);

        const response = await sendRequest(pipe, { mcpTask: 'bigint', data: {} });

        expect(response['status']).toBe('error');
        expect(response['mcpTask']).toBe('bigint');
        expect(response['error']).toMatch(/not JSON-serializable/);
    });

    it('stop() completes promptly when a handler is mid-read', async () => {
        const pipe = makePipeName();
        const controller = new AbortController();
        const service = new WorkerTaskDispatcherService(
            { pipe, readyMarker: '[test] Ready.' },
            [new EchoTask()],
            NullLogger.instance,
        );
        services.push(service);
        controllers.push(controller);
        await service.start(controller.signal);

        // Open a connection but never send a message. The server handler
        // is now blocked inside channel.read() waiting for bytes.
        const socket = net.createConnection(pipe);
        await new Promise<void>((resolve, reject) => {
            socket.once('connect', resolve);
            socket.once('error', reject);
        });

        // stop() must destroy the pending socket so server.close() can
        // resolve instead of hanging forever.
        const t0 = Date.now();
        await service.stop();
        const elapsed = Date.now() - t0;

        expect(elapsed).toBeLessThan(1000);
        socket.destroy();
    });

    it('stop() completes promptly when a task is mid-execute', async () => {
        const pipe = makePipeName();
        const controller = new AbortController();
        const service = new WorkerTaskDispatcherService(
            { pipe, readyMarker: '[test] Ready.' },
            [new HangingTask()],
            NullLogger.instance,
        );
        services.push(service);
        controllers.push(controller);
        await service.start(controller.signal);

        const socket = net.createConnection(pipe);
        await new Promise<void>((resolve, reject) => {
            socket.once('connect', resolve);
            socket.once('error', reject);
        });
        const channel = new LengthPrefixedFrameCodec(socket);
        await channel.write(
            Buffer.from(JSON.stringify({ mcpTask: 'hang', data: {} }), 'utf8'),
        );

        // Let the task actually start executing before stopping.
        await new Promise((r) => setTimeout(r, 30));

        const t0 = Date.now();
        await service.stop();
        const elapsed = Date.now() - t0;

        expect(elapsed).toBeLessThan(1000);
        socket.destroy();
    });

    it.skipIf(process.platform === 'win32')(
        'recovers from a stale unix socket file left over from a previous run',
        async () => {
            const pipe = makePipeName();

            // Create a stale inode at the target path — nothing is
            // listening on it, so it simulates a crashed prior run.
            fs.writeFileSync(pipe, '');

            const controller = new AbortController();
            const service = new WorkerTaskDispatcherService(
                { pipe, readyMarker: '[test] Ready.' },
                [new EchoTask()],
                NullLogger.instance,
            );
            services.push(service);
            controllers.push(controller);

            await service.start(controller.signal);

            const response = await sendRequest(pipe, { mcpTask: 'echo', data: { ok: 1 } });
            expect(response['status']).toBe('ok');
        },
    );

    it.skipIf(process.platform === 'win32')(
        'fails to start when another server is already listening on the unix socket',
        async () => {
            const pipe = makePipeName();

            // Create a live listener occupying the path.
            const blocker = net.createServer();
            onTestFinished(async () => {
                await new Promise<void>((resolve) => blocker.close(() => resolve()));
            });
            await new Promise<void>((resolve, reject) => {
                blocker.once('listening', () => resolve());
                blocker.once('error', reject);
                blocker.listen(pipe);
            });

            const service = new WorkerTaskDispatcherService(
                { pipe, readyMarker: '[test] Ready.' },
                [new EchoTask()],
                NullLogger.instance,
            );
            await expect(service.start(new AbortController().signal)).rejects.toThrow();
        },
    );

    it('threads the request correlationId into CorrelationScope for the task', async () => {
        let observed: string | undefined = 'unset';
        class CapturingTask implements McpTask {
            readonly taskName = 'capture';
            execute(): Promise<unknown> {
                observed = CorrelationScope.current();
                return Promise.resolve({});
            }
        }

        const { pipe } = await startService([new CapturingTask()]);
        const response = await sendRequest(pipe, {
            mcpTask: 'capture',
            data: {},
            correlationId: 'abcd1234',
        });

        expect(response['status']).toBe('ok');
        expect(observed).toBe('abcd1234');
    });

    it('logs task failures with the active correlationId', async () => {
        const calls: Array<{ message: string; correlationId: string | undefined }> = [];
        const logger: LoggerFacade = {
            trace: () => {},
            debug: () => {},
            info: () => {},
            warn: () => {},
            error: (message) => calls.push({ message, correlationId: CorrelationScope.current() }),
        };

        const pipe = makePipeName();
        const controller = new AbortController();
        const service = new WorkerTaskDispatcherService(
            { pipe, readyMarker: '[test] Ready.' },
            [new ThrowingTask()],
            logger,
        );
        services.push(service);
        controllers.push(controller);
        await service.start(controller.signal);

        const response = await sendRequest(pipe, {
            mcpTask: 'throws',
            data: {},
            correlationId: 'feedface',
        });

        expect(response['status']).toBe('error');
        expect(calls.length).toBe(1);
        expect(calls[0]?.message).toMatch(/throws/);
        expect(calls[0]?.correlationId).toBe('feedface');
    });

    it('proceeds without a scope when the request omits correlationId', async () => {
        let observed: string | undefined = 'unset';
        class CapturingTask implements McpTask {
            readonly taskName = 'capture';
            execute(): Promise<unknown> {
                observed = CorrelationScope.current();
                return Promise.resolve({});
            }
        }

        const { pipe } = await startService([new CapturingTask()]);
        await sendRequest(pipe, { mcpTask: 'capture', data: {} });

        expect(observed).toBeUndefined();
    });
});
