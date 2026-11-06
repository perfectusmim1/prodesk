import fs from 'node:fs';
import path from 'node:path';
import { PRODESK_DATA_DIR } from './logger.js';
import type { ToolResult } from '../../shared/src/protocol.js';

export interface MacroStep {
  tool: string;
  params: Record<string, unknown>;
  recordedAt: string;
}

export interface MacroDefinition {
  name: string;
  description?: string;
  createdAt: string;
  steps: MacroStep[];
}

const MACROS_DIR = path.join(PRODESK_DATA_DIR, 'macros');

const NON_RECORDABLE_TOOLS = new Set([
  'macro_record_start',
  'macro_record_stop',
  'macro_run',
  'macro_list',
  'macro_delete',
  'status',
  'session_report',
]);

function sanitizeMacroName(name: string): string {
  const cleaned = name.trim().replace(/[^a-zA-Z0-9_-]/g, '-');
  if (!cleaned) throw new Error('Invalid macro name.');
  return cleaned;
}

export class MacroManager {
  private activeMacro: MacroDefinition | null = null;

  public get isRecording(): boolean {
    return this.activeMacro !== null;
  }

  public startRecording(name: string, description?: string): ToolResult {
    const cleanName = sanitizeMacroName(name);
    this.activeMacro = {
      name: cleanName,
      description,
      createdAt: new Date().toISOString(),
      steps: [],
    };
    return {
      text: `Macro recording started: "${cleanName}". Subsequent actions will be recorded with semantic selectors.`,
      data: { name: cleanName, recording: true },
    };
  }

  public recordStep(tool: string, params: Record<string, unknown>, resolvedSelector?: Record<string, unknown>): void {
    if (!this.activeMacro || NON_RECORDABLE_TOOLS.has(tool)) return;
    const stepParams = { ...params };
    // Enrich with semantic selector if resolved so replay does not depend on static screen coordinates
    if (resolvedSelector && !stepParams.selector) {
      stepParams.selector = resolvedSelector;
      delete stepParams.x;
      delete stepParams.y;
    }
    this.activeMacro.steps.push({
      tool,
      params: stepParams,
      recordedAt: new Date().toISOString(),
    });
  }

  public stopRecording(): ToolResult {
    if (!this.activeMacro) {
      throw new Error('[bad_request] No active macro recording to stop.');
    }
    const macro = this.activeMacro;
    this.activeMacro = null;
    fs.mkdirSync(MACROS_DIR, { recursive: true });
    const filePath = path.join(MACROS_DIR, `${macro.name}.json`);
    fs.writeFileSync(filePath, JSON.stringify(macro, null, 2), 'utf8');
    return {
      text: `Saved macro "${macro.name}" with ${macro.steps.length} step(s) to ${filePath}`,
      data: { name: macro.name, stepCount: macro.steps.length, path: filePath, steps: macro.steps },
    };
  }

  public listMacros(): ToolResult {
    fs.mkdirSync(MACROS_DIR, { recursive: true });
    const files = fs.readdirSync(MACROS_DIR).filter((f) => f.endsWith('.json'));
    const macros = files.map((f) => {
      try {
        const raw = JSON.parse(fs.readFileSync(path.join(MACROS_DIR, f), 'utf8')) as MacroDefinition;
        return {
          name: raw.name,
          description: raw.description ?? '',
          createdAt: raw.createdAt,
          steps: raw.steps?.length ?? 0,
        };
      } catch {
        return { name: f.replace(/\.json$/, ''), description: 'corrupt', createdAt: '', steps: 0 };
      }
    });
    return {
      text: macros.length
        ? macros.map((m) => `- ${m.name} (${m.steps} steps)${m.description ? ': ' + m.description : ''}`).join('\n')
        : 'No saved macros.',
      data: { macros },
    };
  }

  public deleteMacro(name: string): ToolResult {
    const cleanName = sanitizeMacroName(name);
    const filePath = path.join(MACROS_DIR, `${cleanName}.json`);
    if (!fs.existsSync(filePath)) {
      throw new Error(`[not_found] Macro "${cleanName}" not found.`);
    }
    fs.unlinkSync(filePath);
    return { text: `Deleted macro "${cleanName}".`, data: { deleted: cleanName } };
  }

  public loadMacro(name: string): MacroDefinition {
    const cleanName = sanitizeMacroName(name);
    const filePath = path.join(MACROS_DIR, `${cleanName}.json`);
    if (!fs.existsSync(filePath)) {
      throw new Error(`[not_found] Macro "${cleanName}" not found at ${filePath}.`);
    }
    return JSON.parse(fs.readFileSync(filePath, 'utf8')) as MacroDefinition;
  }
}

export function substituteVariables(
  params: Record<string, unknown>,
  variables?: Record<string, string>,
): Record<string, unknown> {
  if (!variables || Object.keys(variables).length === 0) return params;
  const raw = JSON.stringify(params);
  let replaced = raw;
  for (const [k, v] of Object.entries(variables)) {
    const token = k.startsWith('{{') ? k : `{{${k}}}`;
    replaced = replaced.split(token).join(v.replace(/\\/g, '\\\\').replace(/"/g, '\\"'));
  }
  return JSON.parse(replaced) as Record<string, unknown>;
}
