// Bind a bounded assertion review to the source discovery and native checkout.
// Execution results remain in the workflow receipts, never in this review.
export function validateCandidateReview(review, inventory, { blobFor, readNative }) {
  const require = (condition, message) => { if (!condition) throw new Error(message); };
  require(review.format === 'cordis-candidate-delta-review/v1', 'Unknown candidate review format');
  require(review.candidate === inventory.dsh, 'Candidate review targets another revision');
  const identities = new Set(inventory.inventory.map(item => JSON.stringify(item)));
  for (const [file, source] of Object.entries(review.sourceFiles)) {
    require(source.candidateBlob === blobFor(file), 'Reviewed source changed: ' + file);
  }
  const native = new Map(Object.entries(review.nativeFiles).map(([file, hash]) => {
    const actual = readNative(file);
    require(actual.sha256 === hash, 'Reviewed native file changed: ' + file);
    return [file, actual.text];
  }));
  const ids = new Set(), slices = new Set(), cases = new Set();
  for (const cluster of review.clusters) {
    require(!ids.has(cluster.id), 'Duplicate candidate review: ' + cluster.id);
    ids.add(cluster.id);
    slices.add(cluster.slice);
    require(cluster.review === 'reviewed-adaptation', 'Unresolved candidate review: ' + cluster.id);
    require(cluster.assertions.length && cluster.native.length, 'Empty assertion review: ' + cluster.id);
    require(cluster.sourceCases.length || cluster.sourceCaseBoundary, 'Unexplained source case absence: ' + cluster.id);
    for (const identity of cluster.sourceCases) {
      const key = JSON.stringify(identity);
      require(identities.has(key), 'Reviewed case absent from discovery: ' + key);
      require(review.sourceFiles[identity[1]], 'Reviewed case has no source binding: ' + key);
      cases.add(key);
    }
    for (const file of cluster.sourceAlgorithms) {
      require(review.sourceFiles[file], 'Reviewed algorithm has no source binding: ' + file);
    }
    for (const target of cluster.native) {
      require(native.has(target.file), 'Native assertion has no file binding: ' + target.file);
      require(!target.method || native.get(target.file).includes(target.method + '('),
        'Native assertion method missing: ' + target.file + ':' + target.method);
    }
  }
  require(slices.size === 9 && Array.from({ length: 9 }, (_, index) => 'S' + (index + 1)).every(slice => slices.has(slice)),
    'Candidate review must account for every S1-S9 slice');
  return { assertionClusters: ids.size, referencedSourceCases: cases.size,
    sourceFiles: Object.keys(review.sourceFiles).length, nativeFiles: native.size,
    meaning: review.boundary };
}
