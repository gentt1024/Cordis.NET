// Execution discovery cannot decide the scope of an undiscovered historical case.
export function historicalDisposition(repository, file, included, exclusions) {
  const excluded = exclusions.find(item => (item.repository ?? 'dsh') === repository && item.file === file);
  if (included && excluded) throw new Error('A candidate file cannot be both executed and excluded');
  if (excluded && !['excluded', 'source-skipped'].includes(excluded.status)) throw new Error('Unreviewed exclusion status');
  return {
    candidateExecution: included ? 'file-in-runtime-matrix-case-correspondence-unreviewed'
      : excluded?.status === 'source-skipped' ? 'not-executed-source-skipped'
      : excluded ? 'not-executed-outside-native-runtime-scope' : 'not-executed-scope-unreviewed',
    candidateScopeDecision: excluded?.scopeDecision ?? (included ? 'runtime-source-included' : 'unreviewed'),
    candidateScopeReason: excluded?.reason ?? null,
  };
}
