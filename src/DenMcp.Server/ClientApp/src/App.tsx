import { useState, useCallback, useMemo } from 'react';
import type { Message, DocumentSummary } from './api/types';
import { listProjects, listTasks, getMessages, listDocuments, listActiveAgents } from './api/client';
import { usePolling } from './hooks/usePolling';
import { ProjectSidebar } from './components/ProjectSidebar';
import { TaskTree } from './components/TaskTree';
import { TaskDetail } from './components/TaskDetail';
import { FilterBar } from './components/FilterBar';
import { MessageFeed } from './components/MessageFeed';
import { MessageDetail } from './components/MessageDetail';
import { DocumentList } from './components/DocumentList';
import { DocumentDetail } from './components/DocumentDetail';
import { AgentBar } from './components/AgentBar';

export default function App() {
  const [selectedProject, setSelectedProject] = useState<string | null>(null);
  const [selectedTaskId, setSelectedTaskId] = useState<number | null>(null);
  const [selectedMessage, setSelectedMessage] = useState<Message | null>(null);
  const [selectedDoc, setSelectedDoc] = useState<DocumentSummary | null>(null);
  const [viewMode, setViewMode] = useState<'tasks' | 'documents'>('tasks');
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [sortMode, setSortMode] = useState('priority');

  const { data: projects } = usePolling(() => listProjects(), 5000);

  // Auto-select first project
  const effectiveProject = selectedProject
    ?? (projects && projects.length > 0 ? projects[0].id : null);

  const isGlobal = effectiveProject === '_global';

  const fetchTasks = useCallback(
    () => effectiveProject
      ? listTasks(effectiveProject, { tree: true, status: statusFilter ?? undefined })
      : Promise.resolve([]),
    [effectiveProject, statusFilter],
  );
  const { data: tasks } = usePolling(fetchTasks, 5000);

  const fetchMessages = useCallback(
    () => effectiveProject
      ? getMessages(effectiveProject, { limit: 15 })
      : Promise.resolve([]),
    [effectiveProject],
  );
  const { data: messages } = usePolling(fetchMessages, 5000);

  const fetchDocs = useCallback(
    () => effectiveProject
      ? listDocuments(isGlobal ? undefined : effectiveProject)
      : Promise.resolve([]),
    [effectiveProject, isGlobal],
  );
  const { data: documents } = usePolling(fetchDocs, 5000);

  const fetchAgents = useCallback(
    () => listActiveAgents(isGlobal ? undefined : (effectiveProject ?? undefined)),
    [effectiveProject, isGlobal],
  );
  const { data: agents } = usePolling(fetchAgents, 5000);

  const sortedDocs = useMemo(
    () => documents ? [...documents].sort((a, b) => b.updated_at.localeCompare(a.updated_at)) : [],
    [documents],
  );

  const taskCount = tasks?.length ?? 0;
  const filterLabel = statusFilter ? ` [${statusFilter}]` : '';
  const sortLabel = sortMode !== 'priority' ? ` \u2195${sortMode}` : '';

  return (
    <div className="dashboard">
      {/* Messages — top, full width */}
      <div className="panel panel-messages">
        <div className="panel-header">
          Messages {effectiveProject && <span className="count">({messages?.length ?? 0})</span>}
        </div>
        <div className="panel-body">
          <MessageFeed
            messages={messages ?? []}
            isGlobal={isGlobal}
            onSelect={setSelectedMessage}
          />
        </div>
      </div>

      {/* Agents — middle, full width */}
      <div className="panel panel-agents">
        <div className="panel-header">
          Agents <span className="count">({agents?.length ?? 0})</span>
        </div>
        <div className="panel-body" style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center' }}>
          <AgentBar agents={agents ?? []} isGlobal={isGlobal} />
        </div>
      </div>

      {/* Projects sidebar — bottom left */}
      <ProjectSidebar
        projects={projects ?? []}
        selectedId={effectiveProject}
        onSelect={(id) => {
          setSelectedProject(id);
          setSelectedTaskId(null);
          setSelectedDoc(null);
        }}
      />

      {/* Main area — bottom right */}
      <div className="panel panel-main">
        <div className="panel-header">
          {viewMode === 'tasks'
            ? <>Tasks {effectiveProject && <span className="count">({taskCount}{filterLabel}{sortLabel})</span>}</>
            : <>Documents {effectiveProject && <span className="count">({sortedDocs.length})</span>}</>
          }
        </div>
        <FilterBar
          statusFilter={statusFilter}
          onStatusFilterChange={setStatusFilter}
          sortMode={sortMode}
          onSortChange={setSortMode}
          viewMode={viewMode}
          onViewModeChange={setViewMode}
        />
        <div className="panel-body">
          {viewMode === 'tasks' ? (
            <TaskTree
              tasks={tasks ?? []}
              selectedTaskId={selectedTaskId}
              onSelect={setSelectedTaskId}
              statusFilter={statusFilter}
              sortMode={sortMode}
            />
          ) : (
            <DocumentList
              documents={sortedDocs}
              projectId={effectiveProject}
              isGlobal={isGlobal}
              onSelect={setSelectedDoc}
            />
          )}
        </div>
      </div>

      {/* Detail overlays */}
      {selectedTaskId != null && effectiveProject && (
        <TaskDetail
          projectId={effectiveProject}
          taskId={selectedTaskId}
          onClose={() => setSelectedTaskId(null)}
        />
      )}

      {selectedMessage && (
        <MessageDetail
          message={selectedMessage}
          onClose={() => setSelectedMessage(null)}
        />
      )}

      {selectedDoc && (
        <DocumentDetail
          summary={selectedDoc}
          onClose={() => setSelectedDoc(null)}
        />
      )}
    </div>
  );
}
