// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import { workspace, ExtensionContext, DebugAdapterDescriptorFactory, ProviderResult} from 'vscode';
import { ConfigurationFeature } from 'vscode-languageclient/lib/common/configuration';
import {
	Executable,
	LanguageClient,
	LanguageClientOptions,
	ServerOptions,
	TransportKind,
	Trace
} from 'vscode-languageclient/node';
let client: LanguageClient;

// This method is called when your extension is activated
// Your extension is activated the very first time the command is executed
export async function activate(context: vscode.ExtensionContext) {

	// Use the console to output diagnostic information (console.log) and errors (console.error)
	// This line of code will only be executed once when your extension is activated
	console.log('Congratulations, your extension "basicscript" is now active!');

	// register the DAP similar to the sample, https://github.com/microsoft/vscode-mock-debug/blob/main/src/extension.ts
	vscode.debug.registerDebugAdapterDescriptorFactory("fadeBasicDebugger", new FadeBasicDebugger())
	
	console.log('Registered DAP')
	// let path = '/Users/chrishanna/Documents/Github/dby/DarkBasicYo/LSP/bin/Debug/net7.0/LSP.dll'
	
	let config: Executable = {
		command: '/usr/local/share/dotnet/dotnet',
		args: [
			'run',
			'--project',
			'/Users/chrishanna/Documents/Github/dby/FadeBasic/LSP'
		],
		transport: TransportKind.pipe

	}

	// config.args = [path];
	
	const serverOptions: ServerOptions = {
		run: config, debug: config
	}

	const clientOptions: LanguageClientOptions = {
		// Register the server for plain text documents
		documentSelector: [
			{ scheme: 'file', language: 'xml' },
			{ scheme: 'file', language: 'fadeBasic' },
		],
		// progressOnInitialization: true,
		synchronize: {
	
			fileEvents: [
				workspace.createFileSystemWatcher('**/.csproj'),
				workspace.createFileSystemWatcher('**/.fbasic'),
			]
		}
	};

	// Create the language client and start the client.
	client = new LanguageClient(
		'FadeBasicLanguageServer',
		'Fade Basic Language Server',
		serverOptions,
		clientOptions
	);

	client.registerProposedFeatures();
    client.setTrace(Trace.Off);
	if (client.isRunning()){
		console.log('service is already running');
		
	} else {
		await client.start();

	}
}

// This method is called when your extension is deactivated
export async function deactivate() {
	console.log('extension is shutting down');
	await client.stop()
	await client.dispose()
	console.log('extension has shut down');
}

class FadeBasicDebugger implements DebugAdapterDescriptorFactory 
{
	createDebugAdapterDescriptor(_session: vscode.DebugSession, executable: vscode.DebugAdapterExecutable): ProviderResult<vscode.DebugAdapterDescriptor> {
		
		var waitForDebugger = _session.configuration.waitForDebugger ?? false;
		var program = _session.configuration.program;
		var debuggerLogPath = _session.configuration.debuggerLogPath ?? "";
		var dapLogPath = _session.configuration.dapLogPath ?? "";
		executable = new vscode.DebugAdapterExecutable(executable.command, [], {
			env: {
				"FADE_PROGRAM": program,
				"FADE_WAIT_FOR_DEBUG": waitForDebugger,
				"FADE_DEBUGGER_LOG_PATH": debuggerLogPath,
				"FADE_DAP_LOG_PATH": dapLogPath
			}
		});
		console.log('starting fade debugger', _session.configuration)
		return executable;
	}
}