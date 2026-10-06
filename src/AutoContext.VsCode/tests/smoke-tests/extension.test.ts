import * as assert from 'node:assert/strict';
import * as vscode from 'vscode';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { activatedExtension } from './helpers.js';

suite('Extension Smoke Tests', () => {
    test('should be present as an installed extension', () => {
        const ext = vscode.extensions.getExtension('alonsoft.autocontext');

        assert.ok(ext, 'Extension alonsoft.autocontext not found');
    });

    test('should activate the extension', async () => {
        const ext = await activatedExtension();

        assert.ok(ext.isActive, 'Extension did not activate');
    });

    test('should register all AutoContext commands', async () => {
        await activatedExtension();

        const allCommands = await vscode.commands.getCommands(true);
        const expected = [
            'autocontext.auto-configure',
            'autocontext.toggle-instruction',
            'autocontext.reset-instructions',
            'autocontext.enable-instruction',
            'autocontext.disable-instruction',
            'autocontext.enter-export-mode',
            'autocontext.confirm-export',
            'autocontext.cancel-export',
            'autocontext.show-not-detected',
            'autocontext.hide-not-detected',
            'autocontext.delete-override',
            'autocontext.show-original',
            'autocontext.show-changelog',
            'autocontext.show-whats-new',
            'autocontext.start-mcp-worker',
            'autocontext.show-mcp-server-output',
        ];
        const removed = [
            'autocontext.stop-mcp-server',
            'autocontext.restart-mcp-server',
            'autocontext.start-mcp-server',
        ];
        const missing = expected.filter(cmd => !allCommands.includes(cmd));
        const unexpectedlyPresent = removed.filter(cmd => allCommands.includes(cmd));

        assert.ok(missing.length === 0, `Missing commands: ${missing.join(', ')}`);
        assert.ok(unexpectedlyPresent.length === 0, `Unexpected removed commands: ${unexpectedlyPresent.join(', ')}`);
    });

    test('should normalize generated instructions (tags stripped)', async () => {
        const ext = await activatedExtension();
        const generatedDir = join(ext.extensionPath, 'instructions', '.generated');
        assert.ok(existsSync(generatedDir), `.generated directory not found at ${generatedDir}`);
        const files = readdirSync(generatedDir).filter((f: string) => f.endsWith('.md'));
        assert.ok(files.length > 0, '.generated directory contains no instruction files');
        const violations = files.filter(file => {
            const content = readFileSync(join(generatedDir, file), 'utf8');
            return content.length === 0 || /\[INST\d{4}]/.test(content);
        });

        assert.ok(violations.length === 0, `Files with empty content or un-stripped tags: ${violations.join(', ')}`);
    });

    test('should register the autocontext-instructions content provider', async () => {
        await activatedExtension();
        const uri = vscode.Uri.from({ scheme: 'autocontext-instructions', path: 'copilot.instructions.md' });

        const doc = await vscode.workspace.openTextDocument(uri);

        assert.ok(doc.getText().length > 0, 'Content provider returned empty document');
    });
});
