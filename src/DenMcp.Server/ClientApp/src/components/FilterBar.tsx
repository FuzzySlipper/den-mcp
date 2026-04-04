interface Props {
  statusFilter: string | null;
  onStatusFilterChange: (status: string | null) => void;
  sortMode: string;
  onSortChange: (sort: string) => void;
  viewMode: 'tasks' | 'documents';
  onViewModeChange: (mode: 'tasks' | 'documents') => void;
}

const STATUSES = ['planned', 'in_progress', 'review', 'blocked', 'done', 'cancelled'];
const SORTS = ['priority', 'id', 'status', 'title'];

export function FilterBar({
  statusFilter, onStatusFilterChange,
  sortMode, onSortChange,
  viewMode, onViewModeChange,
}: Props) {
  return (
    <div className="filter-bar">
      {viewMode === 'tasks' && (
        <>
          <label>Filter:</label>
          <select
            value={statusFilter ?? ''}
            onChange={e => onStatusFilterChange(e.target.value || null)}
          >
            <option value="">All</option>
            {STATUSES.map(s => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>

          <label>Sort:</label>
          <select value={sortMode} onChange={e => onSortChange(e.target.value)}>
            {SORTS.map(s => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </>
      )}

      <div className="view-toggle">
        <button
          className={viewMode === 'tasks' ? 'active' : ''}
          onClick={() => onViewModeChange('tasks')}
        >
          Tasks
        </button>
        <button
          className={viewMode === 'documents' ? 'active' : ''}
          onClick={() => onViewModeChange('documents')}
        >
          Docs
        </button>
      </div>
    </div>
  );
}
