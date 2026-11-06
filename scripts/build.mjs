/**
 * Builds Prodesk:
 * 1. Bundles packages/server/src/index.ts -> packages/server/dist/index.cjs via esbuild
 * 2. Publishes native/Prodesk.Helper -> single-file win-x64 executable via dotnet publish
 * 3. Copies prodesk-helper.exe into packages/server/dist/prodesk-helper.exe
 */
import { build } from 'esbuild';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const distDir = path.join(root, 'packages/server/dist');
const t0 = Date.now();

fs.mkdirSync(distDir, { recursive: true });

// 1. Bundle TypeScript MCP Server
await build({
  entryPoints: [path.join(root, 'packages/server/src/index.ts')],
  outfile: path.join(distDir, 'index.cjs'),
  bundle: true,
  platform: 'node',
  format: 'cjs',
  target: 'node20',
  external: ['bufferutil', 'utf-8-validate'],
  sourcemap: 'linked',
  logLevel: 'warning',
});
console.log('[prodesk] Bundled MCP server -> packages/server/dist/index.cjs');

// 2. Locate dotnet SDK executable
function resolveDotnet() {
  const localDotnet = path.join(
    process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'),
    'Microsoft',
    'dotnet',
    'dotnet.exe',
  );
  if (fs.existsSync(localDotnet)) {
    try {
      const sdks = execFileSync(localDotnet, ['--list-sdks'], { encoding: 'utf8' }).trim();
      if (sdks) return localDotnet;
    } catch {
      // ignore
    }
  }
  return 'dotnet';
}

const dotnetBin = resolveDotnet();
const helperProj = path.join(root, 'native/Prodesk.Helper/Prodesk.Helper.csproj');
const publishOutDir = path.join(root, 'native/Prodesk.Helper/bin/Publish');

// If prodesk-helper.exe is currently running, stop it gently before overwriting
try {
  execFileSync('taskkill.exe', ['/IM', 'prodesk-helper.exe', '/F'], { stdio: 'ignore' });
} catch {
  // not running
}

console.log('[prodesk] Publishing C# .NET 8 Native Helper (win-x64)...');
execFileSync(
  dotnetBin,
  [
    'publish',
    helperProj,
    '-c',
    'Release',
    '-r',
    'win-x64',
    '--self-contained',
    'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishReadyToRun=true',
    '-o',
    publishOutDir,
  ],
  { stdio: 'inherit' },
);

const publishedExe = path.join(publishOutDir, 'prodesk-helper.exe');
const targetExe = path.join(distDir, 'prodesk-helper.exe');
fs.copyFileSync(publishedExe, targetExe);

console.log(`[prodesk] Build completed in ${Date.now() - t0} ms`);
console.log(`  server -> packages/server/dist/index.cjs`);
console.log(`  helper -> packages/server/dist/prodesk-helper.exe`);
