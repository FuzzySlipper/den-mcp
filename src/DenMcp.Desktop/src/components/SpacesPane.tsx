import { DenSpace } from '../desktop/sidecarBridgeApi';

interface Props {
  spaces: DenSpace[];
}

export function SpacesPane({ spaces }: Props) {
  return (
    <section className="panel spaces-panel">
      <div className="panel-heading">
        <div>
          <p className="eyebrow">Spaces</p>
          <h2>Non-project containers</h2>
        </div>
        <span className="count-pill">{spaces.length}</span>
      </div>
      {spaces.length === 0 ? (
        <p className="muted">No non-project spaces visible.</p>
      ) : (
        <div className="space-card-list">
          {spaces.map((space) => (
            <div className="space-card" key={space.id}>
              <span className="space-title">{space.name}</span>
              <span className="space-meta">
                {space.id} · <span className={`space-kind kind-${space.kind}`}>{space.kind}</span>
                {space.visibility !== 'normal' && (
                  <span className={`space-visibility visibility-${space.visibility}`}> · {space.visibility}</span>
                )}
              </span>
              {space.description && <span className="space-description">{space.description}</span>}
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
