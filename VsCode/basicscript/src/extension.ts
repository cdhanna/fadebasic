// The module 'vscode' contains the VS Code extensibility API
// Import the module and reference it with the alias vscode in your code below
import * as vscode from 'vscode';
import * as util from 'util';
import { workspace, DebugAdapterDescriptorFactory, ProviderResult, DebugConfigurationProvider} from 'vscode';
import {
	Executable,
	LanguageClient,
	LanguageClientOptions,
	ServerOptions,
	TransportKind,
	Trace
} from 'vscode-languageclient/node';
let client: LanguageClient;
let outputChannel: vscode.OutputChannel;

function logMessage(...args: any[]) {
    if (outputChannel) {
        const message = util.format(...args); // Formats like console.log
        outputChannel.appendLine(message);
    }
}

// This method is called when your extension is activated
// Your extension is activated the very first time the command is executed
export async function activate(context: vscode.ExtensionContext) {

	// Use the console to output diagnostic information (console.log) and errors (console.error)
	// This line of code will only be executed once when your extension is activated
	
	outputChannel = vscode.window.createOutputChannel("FadeBasic Logs");
    context.subscriptions.push(outputChannel);

    outputChannel.appendLine("FadeBasic extension activated.");
	
	logMessage('starting fade basic extensions...')
	var workspaceConfig = vscode.workspace.getConfiguration('conf.language.fade');


	var dotnetPath: string = workspaceConfig.get('dotnetPath') || 'dotnet';
	var lspPath : string = vscode.Uri.joinPath(context.extensionUri, 'out', 'tools', 'LSP.dll').fsPath
	var dapPath : string = vscode.Uri.joinPath(context.extensionUri, 'out', 'tools', 'DAP.dll').fsPath

	logMessage(`Fade Basic extension is running. dotnetPath=[${dotnetPath}] lspPath=[${lspPath}] dapPath=[${dapPath}]`);


	vscode.workspace.onDidChangeConfiguration(event => {
		if (event.affectsConfiguration('conf.language.fade.dotnetPath')) {
			vscode.window
				.showInformationMessage("This change requires a restart.", "Restart Now")
				.then(selection => {
					if (selection === "Restart Now") {
					vscode.commands.executeCommand("workbench.action.reloadWindow");
					}
				});
		}
	});
	// register the DAP similar to the sample, https://github.com/microsoft/vscode-mock-debug/blob/main/src/extension.ts
	
	//  the DAP program must be set in the package.json
	// "program": "/Users/chrishanna/Documents/Github/dby/FadeBasic/DAP/bin/Debug/net8.0/DAP",
	
	var dap = new FadeBasicDebugger(dotnetPath, dapPath);
	
	vscode.debug.registerDebugAdapterDescriptorFactory("fadeBasicDebugger", dap)

	
	context.subscriptions.push(vscode.commands.registerCommand('extension.fadeBasic.getProgramName', async config => {
		
		logMessage('picking name', config)
		var path = config["program"];
		var possibleFiles = (await vscode.workspace.findFiles("*.csproj")).map(u => vscode.workspace.asRelativePath(u));
		var fileUris = await possibleFiles;
		
		logMessage('found files', fileUris)
		if (fileUris.length == 1){
			logMessage('using automatic resolution')
			return fileUris[0];
		}

		var res = vscode.window.showQuickPick(fileUris, {
			canPickMany: false
		});
		logMessage('picked', res)
		return res;
	
	}));

	logMessage('Registered DAP')
	// let path = '/Users/chrishanna/Documents/Github/dby/DarkBasicYo/LSP/bin/Debug/net7.0/LSP.dll'
	
	// let config: Executable = {
	// 	command: '/usr/local/share/dotnet/dotnet',
	// 	args: [
	// 		'run',
	// 		'--project',
	// 		'/Users/chrishanna/Documents/Github/dby/FadeBasic/LSP'
	// 	],
	// 	transport: TransportKind.pipe
	// }
		
	let config: Executable = {
		command: dotnetPath,
		args: [
			lspPath
		],
		transport: TransportKind.pipe
	}
	logMessage('fade LSP config', config)
	
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
		logMessage('service is already running');
		
	} else {
		logMessage('starting LSP');
		await client.start();
		logMessage('started LSP');
	}
}

// This method is called when your extension is deactivated
export async function deactivate() {
	logMessage('extension is shutting down');
	await client.stop()
	await client.dispose()
	logMessage('extension has shut down');
}

class FadeBasicDebugger implements DebugAdapterDescriptorFactory
{
	dotnetPath: string;
	dapPath: string;

	constructor(dotnetPath: string, dapPath: string){
		this.dotnetPath = dotnetPath;
		this.dapPath = dapPath;
	}

	createDebugAdapterDescriptor(_session: vscode.DebugSession, executable: vscode.DebugAdapterExecutable): ProviderResult<vscode.DebugAdapterDescriptor> {
	
		var waitForDebugger = _session.configuration.waitForDebugger ?? false;
		var program = _session.configuration.program;
		var debuggerLogPath = _session.configuration.debuggerLogPath ?? "";
		var dapLogPath = _session.configuration.dapLogPath ?? "";

		var env: any = {
			"FADE_PROGRAM": program,
			"FADE_WAIT_FOR_DEBUG": waitForDebugger,
			"FADE_DOTNET_PATH": this.dotnetPath
		}
		if (debuggerLogPath){
			env["FADE_DEBUGGER_LOG_PATH"] = debuggerLogPath
		}
		if (dapLogPath){
			env["FADE_DAP_LOG_PATH"] = dapLogPath
		}

		executable = new vscode.DebugAdapterExecutable(this.dotnetPath, [this.dapPath], {
			env: env
		});
		logMessage('starting fade debugger', this.dotnetPath, _session.configuration)
		return executable;
	}
}