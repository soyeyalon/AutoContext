import * as vscode from 'vscode';
import { existsSync } from 'node:fs';
import { join, normalize } from 'node:path';
import type { ChannelLogger } from 'autocontext-nodejs-core';

/**
 * Subfolder (relative to the extension root) that holds the bundled
 * AutoContext agent-plugin (Claude-format manifest at
 * `.claude-plugin/plugin.json`, hook config at `hooks/hooks.json`,
 * hook scripts at `scripts/`).
 *
 * Registered with VS Code via the `chat.pluginLocations` user setting
 * so the plugin's `SessionStart` hook fires for every chat session,
 * regardless of which workspace is open.
 */
const PLUGIN_SUBFOLDER = 'plugin';

/**
 * VS Code user-setting key (object map of `path → enabled`) consulted
 * by VS Code 1.110+ to discover agent plugins outside the built-in
 * marketplace. Older builds persist the value as inert settings JSON.
 */
const CHAT_CONFIG_SECTION = 'chat';
const PLUGIN_LOCATIONS_KEY = 'pluginLocations';

/**
 * Manages the bundled AutoContext agent-plugin's installation into
 * VS Code's `chat.pluginLocations` user setting.
 *
 * - {@link install} adds the current install's plugin folder and
 *   prunes stale entries left by previous extension versions (each
 *   version installs to a versioned folder, so upgrades would
 *   otherwise accumulate dead entries).
 * - {@link uninstall} removes every entry that points at any version
 *   of this extension. Called from `deactivate()`; best-effort because
 *   VS Code may abort the global-settings write during shutdown.
 *
 * Both operations are best-effort: failures are logged and swallowed
 * so they cannot block activation/deactivation.
 */
export class AgentPluginInstaller {
    private readonly pluginPath: string;

    /**
     * Marker substring used to identify pluginLocations entries that
     * belong to this extension. Derived from `extension.id`
     * (e.g. `alonsoft.autocontext`); each install folder is named
     * `<id>-<version>`, so the trailing hyphen anchors the match.
     */
    private readonly extensionInstallPrefix: string;

    constructor(
        context: vscode.ExtensionContext,
        private readonly log: ChannelLogger,
    ) {
        this.pluginPath = normalize(join(context.extensionPath, PLUGIN_SUBFOLDER));
        this.extensionInstallPrefix = `${context.extension.id.toLowerCase()}-`;
    }

    async install(): Promise<void> {
        if (!existsSync(this.pluginPath)) {
            this.log.warn(`Agent-plugin folder not found at ${this.pluginPath}; skipping ${CHAT_CONFIG_SECTION}.${PLUGIN_LOCATIONS_KEY} install.`);
            return;
        }

        try {
            const config = vscode.workspace.getConfiguration(CHAT_CONFIG_SECTION);
            const current = config.get<Record<string, boolean>>(PLUGIN_LOCATIONS_KEY) ?? {};

            // Build the new map: keep entries that are not ours, drop
            // our own stale entries (previous-version paths that no
            // longer exist on disk), then add/refresh the current
            // install's entry. If the user has explicitly disabled the
            // current install (entry present with `false`), preserve
            // their choice — never silently re-enable.
            const next: Record<string, boolean> = {};
            let preservedDisable = false;
            for (const [entryPath, enabled] of Object.entries(current)) {
                if (!this.isOwnedByThisExtension(entryPath)) {
                    next[entryPath] = enabled;
                    continue;
                }
                const isCurrentInstall = normalize(entryPath) === this.pluginPath;
                if (isCurrentInstall) {
                    next[entryPath] = enabled;
                    if (enabled === false) {
                        preservedDisable = true;
                    }
                } else if (existsSync(entryPath)) {
                    next[entryPath] = enabled;
                } else {
                    this.log.info(`Pruning stale ${CHAT_CONFIG_SECTION}.${PLUGIN_LOCATIONS_KEY} entry: ${entryPath}`);
                }
            }
            if (next[this.pluginPath] === undefined) {
                next[this.pluginPath] = true;
            }

            if (AgentPluginInstaller.shallowEqual(current, next)) {
                if (preservedDisable) {
                    this.log.debug(`${CHAT_CONFIG_SECTION}.${PLUGIN_LOCATIONS_KEY} entry for ${this.pluginPath} is user-disabled; leaving as-is.`);
                } else {
                    this.log.debug(`${CHAT_CONFIG_SECTION}.${PLUGIN_LOCATIONS_KEY} already registers ${this.pluginPath}; no update needed.`);
                }
                return;
            }

            await config.update(PLUGIN_LOCATIONS_KEY, next, vscode.ConfigurationTarget.Global);
            this.log.info(`Installed agent-plugin at ${this.pluginPath} via ${CHAT_CONFIG_SECTION}.${PLUGIN_LOCATIONS_KEY}.`);
        } catch (err) {
            this.log.warn(`Failed to install agent-plugin via ${CHAT_CONFIG_SECTION}.${PLUGIN_LOCATIONS_KEY}: ${err instanceof Error ? err.message : String(err)}`);
        }
    }

    async uninstall(): Promise<void> {
        try {
            const config = vscode.workspace.getConfiguration(CHAT_CONFIG_SECTION);
            const current = config.get<Record<string, boolean>>(PLUGIN_LOCATIONS_KEY) ?? {};

            const next: Record<string, boolean> = {};
            let removed = 0;
            for (const [entryPath, enabled] of Object.entries(current)) {
                if (this.isOwnedByThisExtension(entryPath)) {
                    removed++;
                    continue;
                }
                next[entryPath] = enabled;
            }

            if (removed === 0) {
                return;
            }

            await config.update(PLUGIN_LOCATIONS_KEY, next, vscode.ConfigurationTarget.Global);
            this.log.info(`Removed ${removed} AutoContext entry/entries from ${CHAT_CONFIG_SECTION}.${PLUGIN_LOCATIONS_KEY}.`);
        } catch (err) {
            this.log.warn(`Failed to uninstall agent-plugin from ${CHAT_CONFIG_SECTION}.${PLUGIN_LOCATIONS_KEY}: ${err instanceof Error ? err.message : String(err)}`);
        }
    }

    private isOwnedByThisExtension(entryPath: string): boolean {
        if (normalize(entryPath) === this.pluginPath) {
            return true;
        }
        return entryPath.toLowerCase().includes(this.extensionInstallPrefix);
    }

    private static shallowEqual(a: Record<string, boolean>, b: Record<string, boolean>): boolean {
        const keysA = Object.keys(a);
        const keysB = Object.keys(b);
        if (keysA.length !== keysB.length) {
            return false;
        }
        for (const key of keysA) {
            if (a[key] !== b[key]) {
                return false;
            }
        }
        return true;
    }
}
