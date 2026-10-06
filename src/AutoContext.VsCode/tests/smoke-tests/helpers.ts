import * as assert from 'node:assert/strict';
import * as vscode from 'vscode';

const extensionId = 'alonsoft.autocontext';

// eslint-disable-next-line @typescript-eslint/no-explicit-any
export async function activatedExtension(): Promise<vscode.Extension<any>> {
    const ext = vscode.extensions.getExtension(extensionId);
    assert.ok(ext, `Extension ${extensionId} not found`);
    if (!ext.isActive) {
        await ext.activate();
    }
    return ext;
}
