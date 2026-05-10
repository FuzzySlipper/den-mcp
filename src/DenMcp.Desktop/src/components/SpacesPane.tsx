import { DenSpace } from '../desktop/sidecarBridgeApi';

interface Props {
  spaces: DenSpace[];
  activeSpaceId?: string | null;
  onSelectSpace?: (spaceId: string) => void;
}

function hasRootCapability(space: DenSpace): boolean {
  return Boolean(space.rootPath?.trim());
}

function capabilityLabel(space: DenSpace): string {
  if (space.kind === 'project') return hasRootCapability(space) ? 'repo-backed project' : 'project space';
  return hasRootCapability(space) ? 'root-backed space' : 'space only';
}

export function SpacesPane({ spaces, activeSpaceId, onSelectSpace }: Props) {
  const projectCount = spaces.filter((space) => space.kind === 'project').length;

  return (
    <section className="panel spaces-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Spaces</p>
          <h2>Spaces and projects</h2>
        </div>
        <span className="count-pill">{spaces.length}</span>
      </div>
      <p className="muted">
        Select a space to scope tasks, messages, docs, guidance, and collaboration. Git and terminal snapshots remain project/root-path features.
      </p>
      {spaces.length === 0 ? (
        <p className="muted">No spaces visible from the current Den connection.</p>
      ) : (
        <div className="space-card-list">
          {spaces.map((space) => {
            const active = space.id === activeSpaceId;
            return (
              <button
                type="button"
                className={`space-card space-card-button${active ? ' active' : ''}`}
                key={space.id}
                onClick={() => onSelectSpace?.(space.id)}
                aria-pressed={active}
              >
                <span className="space-title">{space.name || space.id}</span>
                <span className="space-meta">
                  {space.id} · <span className={`space-kind kind-${space.kind}`}>{space.kind}</span>
                  {space.visibility !== 'normal' && (
                    <span className={`space-visibility visibility-${space.visibility}`}> · {space.visibility}</span>
                  )}
                  <span> · {capabilityLabel(space)}</span>
                </span>
                {space.rootPath && <span className="space-description">root {space.rootPath}</span>}
                {space.description && <span className="space-description">{space.description}</span>}
              </button>
            );
          })}
        </div>
      )}
      <p className="muted">{projectCount} project space{projectCount === 1 ? '' : 's'} · {spaces.length - projectCount} non-project space{spaces.length - projectCount === 1 ? '' : 's'}</p>
    </section>
  );
}
