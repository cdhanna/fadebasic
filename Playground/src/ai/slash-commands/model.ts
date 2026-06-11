import type { SlashCommand } from './types';
import { probeWebGPU, formatBytes } from '../webgpu-info';

export const model: SlashCommand = {
    name: 'model',
    description: 'Show the active provider, its capabilities, and WebGPU health.',
    async execute(_args, ctx) {
        const lines: string[] = [];

        // ── Provider section ────────────────────────────────────────────────
        if (!ctx.provider) {
            lines.push('No model loaded. Click "Load Model" in the chat header or open the AI Models tab (Qwen 3 4B).');
        } else {
            const p = ctx.provider;
            const cap = p.capabilities;
            lines.push(`Provider:        ${p.id}`);
            lines.push(`Label:           ${p.label}`);
            lines.push(`Backend:         ${cap.backend ?? '(unknown)'}`);
            lines.push(`Max context:     ${cap.maxContext.toLocaleString()} tokens`);
            lines.push(`Supports tools:  ${cap.supportsTools ? 'native' : 'in-prompt protocol (<tool_call>)'}`);
            lines.push(`Cached locally:  ${cap.isCached ? 'yes' : 'no'}`);
        }

        // ── WebGPU section ──────────────────────────────────────────────────
        lines.push('');
        lines.push('WebGPU adapter:');
        const gpu = await probeWebGPU();
        if (!gpu.available) {
            lines.push(`  Not available — ${gpu.note}`);
        } else {
            if (gpu.vendor)         lines.push(`  Vendor:        ${gpu.vendor}`);
            if (gpu.architecture)   lines.push(`  Architecture:  ${gpu.architecture}`);
            if (gpu.device)         lines.push(`  Device:        ${gpu.device}`);
            if (gpu.description)    lines.push(`  Description:   ${gpu.description}`);
            if (gpu.maxBufferSize)
                lines.push(`  Max buffer:    ${formatBytes(gpu.maxBufferSize)}`);
            if (gpu.maxStorageBufferBindingSize)
                lines.push(`  Max binding:   ${formatBytes(gpu.maxStorageBufferBindingSize)}`);
            if (gpu.maxComputeInvocationsPerWorkgroup)
                lines.push(`  Max compute invocations / workgroup: ${gpu.maxComputeInvocationsPerWorkgroup}`);
        }

        lines.push('');
        lines.push('WebGPU does not expose current memory usage from JS.');
        lines.push('To inspect actual allocation: open Chrome Task Manager (Shift+Esc)');
        lines.push("and read the 'GPU process' row, or visit chrome://gpu.");

        return {
            title: 'Model',
            body: lines.join('\n'),
        };
    },
};
