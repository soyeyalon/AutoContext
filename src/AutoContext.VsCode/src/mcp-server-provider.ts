import * as vscode from 'vscode';
import { join } from 'node:path';
import { existsSync } from 'node:fs';
import type { McpToolsManifest } from './mcp-tools-manifest.js';
import type { AutoContextConfig } from './autocontext-config.js';
import type { ChannelLogger } from 'autocontext-nodejs-core';
import type { McpServerProviderOptions } from '#types/mcp-server-provider-options.js';

const extensionId = 'alonsoft.autocontext';

/**
 * Label of the single `McpStdioServerDefinition` this provider returns.
 * VS Code exposes the definition to the rest of the system via the id
 * `${extensionId}/${mcpServerDefinitionLabel}`, and uses the label as the
 * group header in the chat tools quick-pick — keep it distinct from the
 * extension's display name so it doesn't collide with the LM-tools group.
 */
const mcpServerDefinitionLabel = 'AutoContext MCP Tools';

/**
 * Registers the single `AutoContext.Mcp.Server` MCP surface with VS Code.
 * Mcp.Server fronts every tool across every worker; per-scope gating and
 * per-tool dispatch live inside Mcp.Server itself, so this provider has
 * no knowledge of scopes, workers, or workspace context.
 */
export class McpServerProvider implements vscode.McpServerDefinitionProvider {
    private readonly mcpServerBinary: string;
    private readonly version: string;
    private readonly toolsManifest: McpToolsManifest;
    private readonly instanceId: string;
    private readonly logServiceAddress: string;
    private readonly healthMonitorServiceAddress: string;
    private readonly workerControlServiceAddress: string;
    private readonly extensionConfigServiceAddress: string;
    private readonly logger: ChannelLogger;
    private _config: AutoContextConfig;
    private readonly disposable: vscode.Disposable;

    readonly onDidChangeMcpServerDefinitions: vscode.Event<void>;

    constructor(options: McpServerProviderOptions) {
        const {
            extensionPath,
            version,
            onDidChange,
            toolsManifest,
            serversManifest,
            configManager,
            instanceId,
            logServiceAddress,
            healthMonitorServiceAddress,
            workerControlServiceAddress,
            extensionConfigServiceAddress,
            logger,
        } = options;

        const mcpServerEntry = serversManifest.mcpServer;
        const ext = process.platform === 'win32' ? '.exe' : '';
        this.mcpServerBinary = join(extensionPath, 'servers', mcpServerEntry.name, `${mcpServerEntry.name}${ext}`);
        this.version = version;
        this.toolsManifest = toolsManifest;
        this.instanceId = instanceId;
        this.logServiceAddress = logServiceAddress;
        this.healthMonitorServiceAddress = healthMonitorServiceAddress;
        this.workerControlServiceAddress = workerControlServiceAddress;
        this.extensionConfigServiceAddress = extensionConfigServiceAddress;
        this.logger = logger;
        this._config = configManager.readSync();
        this.onDidChangeMcpServerDefinitions = onDidChange;
        this.disposable = configManager.onDidChange(() => {
            void configManager.read().then(c => { this._config = c; }).catch(err =>
                this.logger.error('Failed to update config', err),
            );
        });
    }

    dispose(): void {
        this.disposable.dispose();
    }

    async provideMcpServerDefinitions(): Promise<vscode.McpServerDefinition[]> {
        if (!existsSync(this.mcpServerBinary)) {
            this.logger.warn(`Mcp.Server binary not found at '${this.mcpServerBinary}'; returning no MCP server definitions`);
            return [];
        }
        if (!this.anyToolEnabled()) {
            this.logger.debug('No tools enabled in config; returning no MCP server definitions');
            return [];
        }

        // --instance-id is the per-window identity Mcp.Server uses to
        //   self-format its outbound worker addresses (one source of
        //   truth shared with the extension and every worker).
        // --service log=<address> streams Mcp.Server's structured logs
        //   to the extension's LogServer (own AutoContext Output channel).
        // --service health-monitor=<address> announces Mcp.Server's
        //   liveness; held open for the lifetime of the host.
        // --service worker-control=<address> lets Mcp.Server ask the
        //   extension to ensure a worker is running before it
        //   dispatches a tool call.
        // --service extension-config=<address> subscribes Mcp.Server
        //   to the extension's AutoContextConfigServer so it learns
        //   which tools/tasks the user has disabled in the tree (and
        //   filters tools/list + dispatch accordingly).
        const args: string[] = [
            '--instance-id', this.instanceId,
            '--service', `log=${this.logServiceAddress}`,
            '--service', `health-monitor=${this.healthMonitorServiceAddress}`,
            '--service', `worker-control=${this.workerControlServiceAddress}`,
            '--service', `extension-config=${this.extensionConfigServiceAddress}`,
        ];

        this.logger.debug(`Returning Mcp.Server definition '${mcpServerDefinitionLabel}' (v${this.version})`);
        return [new vscode.McpStdioServerDefinition(
            mcpServerDefinitionLabel,
            this.mcpServerBinary,
            args,
            undefined,
            this.version,
        )];
    }

    async resolveMcpServerDefinition(server: vscode.McpServerDefinition): Promise<vscode.McpServerDefinition> {
        return server;
    }

    /**
     * Returns the availability status of the (single) MCP server for use in
     * UI indicators on a top-level category node. Every category currently
     * resolves to the same Mcp.Server binary; the parameter is reserved for
     * a future per-category server split.
     *
     * - `'unavailable'`: the Mcp.Server binary does not exist on disk.
     * - `'disabled'`: the binary exists but every tool is disabled in settings.
     * - `'available'`: the binary exists and at least one tool is enabled.
     */
    getServerStatus(_topCategoryName: string): 'unavailable' | 'disabled' | 'available' {
        if (!existsSync(this.mcpServerBinary)) {
            return 'unavailable';
        }
        return this.anyToolEnabled() ? 'available' : 'disabled';
    }

    /**
     * Returns the VS Code internal definition IDs a tree node's command
     * should target. All categories map to the single Mcp.Server
     * definition id until per-category servers are introduced.
     */
    getDefinitionIds(_topCategoryName: string): string[] {
        return [`${extensionId}/${mcpServerDefinitionLabel}`];
    }

    private anyToolEnabled(): boolean {
        for (const tool of this.toolsManifest.tools) {
            if (tool.tasks.length === 0) {
                if (this._config.isToolEnabled(tool.name)) { return true; }
                continue;
            }
            for (const task of tool.tasks) {
                if (this._config.isToolEnabled(tool.name, task.name)) { return true; }
            }
        }
        return false;
    }
}
