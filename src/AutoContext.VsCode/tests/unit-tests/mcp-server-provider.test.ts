import { describe, it, expect, vi, beforeEach } from 'vitest';
import { join } from 'node:path';
import { McpStdioServerDefinition } from '#support/fake-vscode';
import { McpServerProvider } from '#src/mcp-server-provider';
import { McpToolsManifestLoader } from '#src/mcp-tools-manifest-loader';
import { ServersManifest } from '#src/servers-manifest';
import { ServerEntry } from '#src/server-entry';
import { AutoContextConfig } from '#src/autocontext-config.js';
import { createFakeConfigManager } from '#support/fake-config-manager';
import { createFakeDetector } from '#support/fake-detector';
import { createFakeLogger } from '#support/fake-logger';

const { existsSyncMock } = vi.hoisted(() => ({ existsSyncMock: vi.fn<(path: string) => boolean>(() => true) }));
vi.mock('node:fs', async () => {
    const actual = await vi.importActual<typeof import('node:fs')>('node:fs');
    return { ...actual, existsSync: existsSyncMock };
});

type StdioDef = InstanceType<typeof McpStdioServerDefinition>;

const extensionPath = '/ext';
const version = '1.0.0';

const logger = createFakeLogger();

const onDidChange = vi.fn() as unknown as import('vscode').Event<void>;
const mcpToolsManifest = new McpToolsManifestLoader(join(__dirname, '..', '..'), {
    detector: createFakeDetector(),
    configManager: createFakeConfigManager(),
}).load();
const serversManifest: ServersManifest = new ServersManifest([
    new ServerEntry('mcp-server', 'AutoContext.Mcp.Server', 'dotnet'),
]);
const INSTANCE_ID = 'abc123def456';

let currentConfig: AutoContextConfig = new AutoContextConfig();
const fakeConfigManager = createFakeConfigManager();

function createProvider(): McpServerProvider {
    return new McpServerProvider({
        extensionPath,
        version,
        onDidChange,
        toolsManifest: mcpToolsManifest,
        serversManifest,
        configManager: fakeConfigManager,
        instanceId: INSTANCE_ID,
        logServiceAddress: `autocontext.log#${INSTANCE_ID}`,
        healthMonitorServiceAddress: `autocontext.health-monitor#${INSTANCE_ID}`,
        workerControlServiceAddress: `autocontext.worker-control#${INSTANCE_ID}`,
        extensionConfigServiceAddress: `autocontext.extension-config#${INSTANCE_ID}`,
        logger,
    });
}

beforeEach(() => {
    vi.clearAllMocks();
    currentConfig = new AutoContextConfig();
    vi.mocked(fakeConfigManager.readSync).mockImplementation(() => currentConfig);
    vi.mocked(fakeConfigManager.onDidChange).mockReturnValue({ dispose: vi.fn() });
    existsSyncMock.mockReturnValue(true);
});

function buildAllDisabledConfig(): AutoContextConfig {
    const mcpToolsConfig: NonNullable<AutoContextConfig['mcpTools']> = {};
    for (const tool of mcpToolsManifest.tools) {
        if (tool.tasks.length === 0) {
            mcpToolsConfig[tool.name] = false;
        } else {
            mcpToolsConfig[tool.name] = { disabledTasks: tool.tasks.map(t => t.name) };
        }
    }
    return new AutoContextConfig({ mcpTools: mcpToolsConfig });
}

describe('McpServerProvider.provideMcpServerDefinitions', () => {
    it('should return a single definition when binary exists and any tool is enabled', async () => {
        const defs = await createProvider().provideMcpServerDefinitions();

        expect(defs).toHaveLength(1);
        expect(defs[0]).toBeInstanceOf(McpStdioServerDefinition);
    });

    it('should return an empty list when the Mcp.Server binary does not exist', async () => {
        existsSyncMock.mockReturnValue(false);

        const defs = await createProvider().provideMcpServerDefinitions();

        expect(defs).toHaveLength(0);
    });

    it('should return an empty list when every tool is disabled', async () => {
        currentConfig = buildAllDisabledConfig();

        const defs = await createProvider().provideMcpServerDefinitions();

        expect(defs).toHaveLength(0);
    });

    it('should resolve to AutoContext.Mcp.Server binary', async () => {
        const [def] = (await createProvider().provideMcpServerDefinitions()) as StdioDef[];

        expect(def.command).toContain('AutoContext.Mcp.Server');
    });

    it('should pass --instance-id with the configured value', async () => {
        const [def] = (await createProvider().provideMcpServerDefinitions()) as StdioDef[];

        expect(def.args).toEqual(expect.arrayContaining(['--instance-id', INSTANCE_ID]));
    });

    it('should pass --service log=<address> with the LogServer pipe name', async () => {
        const [def] = (await createProvider().provideMcpServerDefinitions()) as StdioDef[];

        expect(def.args).toEqual(expect.arrayContaining(['--service', `log=autocontext.log#${INSTANCE_ID}`]));
    });

    it('should pass --service health-monitor=<address> with the HealthMonitorServer pipe name', async () => {
        const [def] = (await createProvider().provideMcpServerDefinitions()) as StdioDef[];

        expect(def.args).toEqual(expect.arrayContaining(['--service', `health-monitor=autocontext.health-monitor#${INSTANCE_ID}`]));
    });

    it('should pass --service worker-control=<address> with the WorkerControlServer pipe name', async () => {
        const [def] = (await createProvider().provideMcpServerDefinitions()) as StdioDef[];

        expect(def.args).toEqual(expect.arrayContaining(['--service', `worker-control=autocontext.worker-control#${INSTANCE_ID}`]));
    });

    it('should pass --service extension-config=<address> with the AutoContextConfigServer pipe name', async () => {
        const [def] = (await createProvider().provideMcpServerDefinitions()) as StdioDef[];

        expect(def.args).toEqual(expect.arrayContaining(['--service', `extension-config=autocontext.extension-config#${INSTANCE_ID}`]));
    });

    it('should not pass --scope, --workspace-folder, or --workspace-server', async () => {
        const [def] = (await createProvider().provideMcpServerDefinitions()) as StdioDef[];

        expect(def.args).not.toContain('--scope');
        expect(def.args).not.toContain('--workspace-folder');
        expect(def.args).not.toContain('--workspace-server');
    });

    it('should carry the extension version', async () => {
        const [def] = (await createProvider().provideMcpServerDefinitions()) as StdioDef[];

        expect(def.version).toBe(version);
    });
});

describe('McpServerProvider.getServerStatus', () => {
    it('should return available when the binary exists and at least one tool is enabled', () => {
        expect(createProvider().getServerStatus('.NET')).toBe('available');
    });

    it('should return unavailable when the binary does not exist', () => {
        existsSyncMock.mockReturnValue(false);

        expect(createProvider().getServerStatus('.NET')).toBe('unavailable');
    });

    it('should return disabled when every tool is turned off in settings', () => {
        currentConfig = buildAllDisabledConfig();

        expect(createProvider().getServerStatus('.NET')).toBe('disabled');
    });

    it('should return the same status regardless of the legacy label passed', () => {
        const provider = createProvider();

        expect(provider.getServerStatus('.NET')).toBe('available');
        expect(provider.getServerStatus('Web')).toBe('available');
        expect(provider.getServerStatus('Workspace')).toBe('available');
        expect(provider.getServerStatus('Anything')).toBe('available');
    });
});

describe('McpServerProvider.getDefinitionIds', () => {
    it('should return the single Mcp.Server definition id for any label', () => {
        const provider = createProvider();
        const expected = ['alonsoft.autocontext/AutoContext MCP Tools'];

        expect(provider.getDefinitionIds('.NET')).toEqual(expected);
        expect(provider.getDefinitionIds('Web')).toEqual(expected);
        expect(provider.getDefinitionIds('Workspace')).toEqual(expected);
        expect(provider.getDefinitionIds('Unknown')).toEqual(expected);
    });
});

describe('McpServerProvider config updates', () => {
    it('should log to the output channel when configManager.read rejects', async () => {
        let onDidChangeCallback!: () => void;
        const failingConfigManager = {
            readSync: vi.fn(() => new AutoContextConfig()),
            read: vi.fn().mockRejectedValue(new Error('read boom')),
            onDidChange: vi.fn((cb: () => void) => { onDidChangeCallback = cb; return { dispose: vi.fn() }; }),
        } as unknown as import('../../src/autocontext-config-manager').AutoContextConfigManager;

        const oc = createFakeLogger();
        const provider = new McpServerProvider({
            extensionPath,
            version,
            onDidChange,
            toolsManifest: mcpToolsManifest,
            serversManifest,
            configManager: failingConfigManager,
            instanceId: INSTANCE_ID,
            logServiceAddress: `autocontext.log#${INSTANCE_ID}`,
            healthMonitorServiceAddress: `autocontext.health-monitor#${INSTANCE_ID}`,
            workerControlServiceAddress: `autocontext.worker-control#${INSTANCE_ID}`,
            extensionConfigServiceAddress: `autocontext.extension-config#${INSTANCE_ID}`,
            logger: oc,
        });

        onDidChangeCallback();
        await vi.waitFor(() => {
            expect(oc.error).toHaveBeenCalledWith(
                'Failed to update config',
                expect.objectContaining({ message: 'read boom' }),
            );
        });

        provider.dispose();
    });
});
