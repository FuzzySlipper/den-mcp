/**
 * Standalone compiled response formatter for collaboration annotations.
 *
 * Extracted from useCollaborationState so it can be imported by tests without
 * dragging in the full denCollaborationApi type chain. Inline type definitions
 * mirror the snake_case JSON shape from the Den REST API.
 */

export interface CompileSegment {
  id: number;
  sequence_number: number;
  segment_hash: string;
  segment_type: string;
  raw_markdown: string;
  text: string | null;
}

export interface CompileAnnotation {
  segment_id: number;
  annotation_type: string;
  body: string | null;
}

function buildSnippet(segment: CompileSegment): string {
  if (segment.segment_type === 'code_block') {
    const text = segment.text ?? segment.raw_markdown;
    const firstLine = text.split('\n')[0];
    const truncated = firstLine.length > 50 ? firstLine.slice(0, 50) + '...' : firstLine;
    return `[code block: ${truncated}]`;
  }
  const rawText = segment.text ?? segment.raw_markdown;
  return rawText.length > 80 ? rawText.slice(0, 80) + '...' : rawText;
}

function formatAnnotationLine(annotation: CompileAnnotation): string {
  const prefix = annotation.annotation_type === 'skip'
    ? '[skip — no response needed]'
    : annotation.annotation_type === 'done'
      ? '[done — already handled]'
      : annotation.annotation_type === 'flag'
        ? '[FLAG]'
        : '[note]';

  if (annotation.annotation_type === 'skip') return `  ${prefix}`;
  if (annotation.annotation_type === 'flag') {
    const body = annotation.body ? `: ${annotation.body.trim()}` : ': needs discussion';
    return `  ${prefix}${body}`;
  }
  if (annotation.body) return `  ${prefix}: ${annotation.body.trim()}`;
  if (annotation.annotation_type === 'done') return '  [done — already handled]';
  if (annotation.annotation_type === 'note') return '  [note]: acknowledged';
  return `  ${prefix}`;
}

export function compileResponse(
  segments: CompileSegment[],
  annotations: CompileAnnotation[],
): string {
  const bySegmentId = new Map<number, CompileAnnotation[]>();
  for (const ann of annotations) {
    const list = bySegmentId.get(ann.segment_id) ?? [];
    list.push(ann);
    bySegmentId.set(ann.segment_id, list);
  }

  const lines: string[] = [];
  let anyAnnotated = false;

  for (const segment of segments) {
    const segAnns = bySegmentId.get(segment.id);
    if (!segAnns || segAnns.length === 0) continue;
    anyAnnotated = true;

    const hashPrefix = segment.segment_hash.length >= 8
      ? segment.segment_hash.slice(0, 8)
      : segment.segment_hash;
    lines.push(`> [segment ${segment.sequence_number} · ${hashPrefix}] ${buildSnippet(segment)}`);
    for (const ann of segAnns) {
      lines.push(formatAnnotationLine(ann));
    }
    lines.push('');
  }

  const annotatedIds = new Set(annotations.map((a) => a.segment_id));
  const unannotatedCount = segments.filter((s) => !annotatedIds.has(s.id)).length;

  if (!anyAnnotated) {
    lines.push('[no annotations — acknowledged in full, proceed]');
  } else if (unannotatedCount > 0) {
    lines.push('---');
    lines.push(`[${unannotatedCount} section(s) not annotated — treat as acknowledged, proceed with flagged items]`);
  }

  return lines.join('\n');
}
