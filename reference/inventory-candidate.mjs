// Build a separate candidate inventory from complete runtime discovery. Never
// infer a pass or copy assertion-review status across changed upstream source.
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { resolve, relative } from 'node:path';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { checkout, manifest } from './candidate-support.mjs';
import { historicalDisposition } from './candidate-inventory-scope.mjs';
import { validateCandidateReview } from './candidate-review.mjs';
const options = Object.fromEntries(process.argv.slice(2).map(arg => {
  const separator = arg.indexOf('=');
  if (!arg.startsWith('--') || separator < 0) throw new Error('Use --inventory= --dsh= --origin= --output=');
  return [arg.slice(2, separator), arg.slice(separator + 1)];
}));
for (const name of ['inventory', 'dsh', 'origin', 'output']) if (!options[name]) throw new Error('Missing --' + name);
const target = resolve(options.output);
if (existsSync(target)) throw new Error('Do not overwrite candidate inventory evidence');
const dsh = checkout(options.dsh, manifest.dsh);
const origin = checkout(options.origin, manifest.origin);
const frozen = JSON.parse(readFileSync(options.inventory, 'utf8'));
if (frozen.dsh !== manifest.dsh || frozen.origin !== manifest.origin) throw new Error('Foreign runtime discovery');
const sha256 = value => createHash('sha256').update(value).digest('hex');
const counts = JSON.parse(readFileSync(new URL('./candidate-suite-counts.json', import.meta.url), 'utf8'));
if (counts.dsh !== manifest.dsh || counts.origin !== manifest.origin || counts.manifestSha256 !== sha256(readFileSync(new URL('./candidate-manifest.json', import.meta.url)))) throw new Error('Foreign candidate count inventory');
const observed = new Map();
for (const [suite, file] of frozen.inventory) { const key = suite + ':' + file; observed.set(key, (observed.get(key) || 0) + 1); }
const expected = new Map(Object.entries(counts.suites).flatMap(([suite, files]) => Object.entries(files).map(([file, count]) => [suite + ':' + file, count])));
if (observed.size !== expected.size || [...expected].some(([key, count]) => observed.get(key) !== count)) throw new Error('Incomplete candidate runtime discovery');
const historical = JSON.parse(readFileSync(new URL('../docs/upstream-tests.json', import.meta.url), 'utf8'));
const componentMaps = execFileSync('git', ['ls-files', 'tests/*/*map.json'], { encoding: 'utf8' }).trim().split('\n').filter(Boolean);
const legacyMappings = new Map();
for (const path of componentMaps) {
  const component = JSON.parse(readFileSync(path, 'utf8'));
  for (const record of component.tests) {
    const rows = legacyMappings.get(record.id) || [];
    rows.push({ mappingSource: path, ...record });
    legacyMappings.set(record.id, rows);
  }
}
const runtimeFiles = new Set(frozen.inventory.map(([suite, file]) => manifest.suites[suite].repository + ':' + file));
const blobs = new Map();
const gitBlob = (root, revision, file) => {
  const key = root + ':' + revision + ':' + file;
  if (blobs.has(key)) return blobs.get(key);
  try { const oid = execFileSync('git', ['-C', root, 'rev-parse', '--verify', revision + ':' + file], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim(); blobs.set(key, oid); return oid; }
  catch { return null; }
};
const history = historical.tests.map(record => {
  const repository = record.sourceRepository === 'cordiverse/cordis' ? 'origin' : 'dsh';
  const root = repository === 'origin' ? origin : dsh;
  const pin = repository === 'origin' ? manifest.origin : manifest.dsh;
  const blob = gitBlob(root, pin, record.file);
  const oldBlob = gitBlob(root, record.sourceCommit, record.file);
  const disposition = historicalDisposition(repository, record.file, runtimeFiles.has(repository + ':' + record.file), manifest.excludedSourceTests);
  return { ...record, historicalTargetCommit: record.targetCommit,
    candidateSourceCommit: pin, candidateSourceBlob: blob,
    sourceUnchanged: Boolean(blob && oldBlob && blob === oldBlob),
    ...disposition,
    candidateMappingStatus: 'requires-candidate-assertion-review',
    historicalMappings: legacyMappings.get(record.id) || [],
    meaning: 'Historical record retained verbatim. Its native disposition is historical; file inclusion does not prove this candidate assertion instance passed or was ported.' };
});
const reviewBytes = readFileSync(new URL('../docs/candidate-delta-review.json', import.meta.url));
const review = JSON.parse(reviewBytes);
const formal = JSON.parse(readFileSync(new URL('../upstream.lock.json', import.meta.url), 'utf8'));
if (review.formal !== formal.harness.commit) throw new Error('Candidate review has a different formal baseline');
const nativeDeltaReview = validateCandidateReview(review, frozen, {
  blobFor: file => gitBlob(dsh, manifest.dsh, file),
  readNative: file => { const bytes = readFileSync(new URL('../' + file, import.meta.url));
    return { sha256: sha256(bytes), text: bytes.toString('utf8') }; },
});
const fileHashes = new Map();
const sourceHash = path => { if (!fileHashes.has(path)) fileHashes.set(path, sha256(readFileSync(path))); return fileHashes.get(path); };
const tests = frozen.inventory.map(([suite, file, name, ordinal]) => {
  const repository = manifest.suites[suite].repository;
  const root = repository === 'origin' ? origin : dsh;
  const pin = repository === 'origin' ? manifest.origin : manifest.dsh;
  const sourceRepository = repository === 'origin' ? 'cordiverse/cordis' : 'deepseek-ai/deepseek-harness';
  const blob = gitBlob(root, pin, file);
  if (!blob || frozen.sourceFiles[suite][file] !== blob) throw new Error('Unbound candidate source file: ' + file);
  return { id: 'C-' + sha256(sourceRepository + ':' + file + ':' + name + ':' + ordinal).slice(0, 16),
    case: [suite, file, name, ordinal], targetCommit: manifest.dsh, sourceRepository,
    sourceCommit: pin, sourceBlob: blob, title: name, occurrence: ordinal,
    sourceSha256: sourceHash(resolve(root, file)), kind: 'original-runtime-test-instance',
    status: 'discovered-not-executed', dotnet: [], assertionReviewStatus: 'unreviewed', evidence: [],
    historicalFileRecordIds: history.filter(record => record.sourceRepository === sourceRepository && record.file === file).map(record => record.id) };
});
if (new Set(tests.map(test => test.id)).size !== tests.length) throw new Error('Duplicate candidate stable identity');
writeFileSync(target, JSON.stringify({ format: 'cordis-candidate-inventory/v1', targetCommit: manifest.dsh,
  formalBaselineUnchanged: true, discoverySha256: sha256(readFileSync(options.inventory)), runId: frozen.runId,
  meaning: 'Runtime instance discovery and retained historical scope. Execution receipts and native assertion correspondence remain separate.',
  tests, historicalTests: history, excludedSourceTests: manifest.excludedSourceTests,
  nativeDeltaReview: { sha256: sha256(reviewBytes), ...nativeDeltaReview },
  counts: { runtimeInstances: tests.length, retainedHistoricalInstances: history.length,
    historicalOutsideRuntime: history.filter(record => record.candidateExecution === 'not-executed-outside-native-runtime-scope').length,
    historicalScopeUnreviewed: history.filter(record => record.candidateExecution === 'not-executed-scope-unreviewed').length,
    historicalSourceSkipped: history.filter(record => record.candidateExecution === 'not-executed-source-skipped').length,
    nativeAssertionSetsReviewed: 0 } }, null, 2) + '\n');
console.log(`${tests.length} candidate runtime instances; ${history.length} historical records retained. No pass status inferred.`);
