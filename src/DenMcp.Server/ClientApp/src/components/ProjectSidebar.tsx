import type { Project } from '../api/types';

interface Props {
  projects: Project[];
  selectedId: string | null;
  onSelect: (id: string) => void;
}

export function ProjectSidebar({ projects, selectedId, onSelect }: Props) {
  return (
    <div className="panel panel-projects">
      <div className="panel-header">
        Projects <span className="count">{projects.length}</span>
      </div>
      <div className="panel-body">
        {projects.map(p => (
          <div
            key={p.id}
            className={`list-item${p.id === selectedId ? ' selected' : ''}`}
            onClick={() => onSelect(p.id)}
          >
            {p.id}
          </div>
        ))}
      </div>
    </div>
  );
}
