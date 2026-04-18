import { useState, useCallback, useMemo } from 'react';
import type { Message, DocumentSummary, MessageIntent, DispatchEntry } from './api/types';
import {
  listProjects,
  listTasks,
  getMessageFeed,
  listDocuments,
  listActiveAgents,
  listDispatches,
  approveDispatch,
  rejectDispatch,
} from './api/client';
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
import { DispatchPanel } from './components/DispatchPanel';
import { MESSAGE_INTENT_OPTIONS, messageIntentLabel } from './messageIntents';

export default function App() {
  const [selectedProject, setSelectedProject] = useState<string | null>(null);
  const [selectedTaskId, setSelectedTaskId] = useState<number | null>(null);
  const [selectedMessage, setSelectedMessage] = useState<Message | null>(null);
  const [selectedDoc, setSelectedDoc] = useState<DocumentSummary | null>(null);
  const [viewMode, setViewMode] = useState<'tasks' | 'documents'>('tasks');
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [messageIntentFilter, setMessageIntentFilter] = useState<MessageIntent | ''>('');
  const [sortMode, setSortMode] = useState('priority');
  const [pendingDispatchActionId, setPendingDispatchActionId] = useState<number | null>(null);
  const [dispatchActionError, setDispatchActionError] = useState<string | null>(null);

  const fetchProjects = useCallback(() => listProjects(), []);
  const { data: projects } = usePolling(fetchProjects, 5000);

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
      ? getMessageFeed(effectiveProject, {
        limit: 15,
        intent: messageIntentFilter || undefined,
      })
      : Promise.resolve([]),
    [effectiveProject, messageIntentFilter],
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

  const fetchDispatches = useCallback(
    () => effectiveProject
      ? listDispatches({
        projectId: isGlobal ? undefined : effectiveProject,
        status: 'pending',
      })
      : Promise.resolve([]),
    [effectiveProject, isGlobal],
  );
  const {
    data: dispatches,
    error: dispatchError,
    refresh: refreshDispatches,
  } = usePolling(fetchDispatches, 5000);

  const sortedDocs = useMemo(
    () => documents ? [...documents].sort((a, b) => b.updated_at.localeCompare(a.updated_at)) : [],
    [documents],
  );

  const taskCount = tasks?.length ?? 0;
  const filterLabel = statusFilter ? ` [${statusFilter}]` : '';
  const messageFilterLabel = messageIntentFilter ? ` [${messageIntentFilter}]` : '';
  const sortLabel = sortMode !== 'priority' ? ` \u2195${sortMode}` : '';

  const handleProjectSelect = useCallback((id: string) => {
    setSelectedProject(id);
    setSelectedTaskId(null);
    setSelectedMessage(null);
    setSelectedDoc(null);
  }, []);

  const handleTaskSelect = useCallback((taskId: number) => {
    setSelectedTaskId(taskId);
    setSelectedMessage(null);
    setSelectedDoc(null);
  }, []);

  const handleMessageSelect = useCallback((message: Message) => {
    setSelectedMessage(message);
    setSelectedDoc(null);
  }, []);

  const handleDispatchDecision = useCallback(async (
    dispatch: DispatchEntry,
    decision: 'approve' | 'reject',
  ) => {
    const verb = decision === 'approve' ? 'approve' : 'reject';
    const confirmed = window.confirm(
      `${verb[0].toUpperCase()}${verb.slice(1)} dispatch #${dispatch.id} for ${dispatch.target_agent} on ${dispatch.project_id}?`,
    );
    if (!confirmed) {
      return;
    }

    setPendingDispatchActionId(dispatch.id);
    setDispatchActionError(null);

    try {
      if (decision === 'approve') {
        await approveDispatch(dispatch.id, 'web-ui');
      } else {
        await rejectDispatch(dispatch.id, 'web-ui');
      }
    } catch (error) {
      setDispatchActionError(error instanceof Error ? error.message : String(error));
    } finally {
      setPendingDispatchActionId(null);
      refreshDispatches();
    }
  }, [refreshDispatches]);

  const visibleDispatchError = dispatchActionError ?? (dispatchError ? dispatchError.message : null);

  return (
    <div className="dashboard">
      {/* Messages — top, full width */}
      <div className="panel panel-messages">
        <div className="panel-header">
          Messages {effectiveProject && <span className="count">({messages?.length ?? 0}{messageFilterLabel})</span>}
          <span className="header-spacer" />
          <label className="panel-filter-label" htmlFor="message-intent-filter">Intent</label>
          <select
            id="message-intent-filter"
            className="panel-filter-select"
            value={messageIntentFilter}
            onChange={e => setMessageIntentFilter((e.target.value as MessageIntent) || '')}
            title={messageIntentFilter ? `Filtering messages by ${messageIntentLabel(messageIntentFilter)}` : 'Show all message intents'}
          >
            <option value="">All</option>
            {MESSAGE_INTENT_OPTIONS.map(option => (
              <option key={option.value} value={option.value}>{option.label}</option>
            ))}
          </select>
        </div>
        <div className="panel-body">
          <MessageFeed
            messages={messages ?? []}
            isGlobal={isGlobal}
            onSelect={handleMessageSelect}
          />
        </div>
      </div>

      {/* Middle row — agents plus dispatch fallback controls */}
      <div className="panel-middle-grid">
        <div className="panel panel-agents">
          <div className="panel-header">
            Agents <span className="count">({agents?.length ?? 0})</span>
          </div>
          <div className="panel-body panel-body-agents">
            <AgentBar agents={agents ?? []} isGlobal={isGlobal} />
          </div>
        </div>

        <div className="panel panel-dispatches">
          <div className="panel-header">
            Dispatches <span className="count">({dispatches?.length ?? 0})</span>
          </div>
          <div className="panel-body">
            <DispatchPanel
              dispatches={dispatches ?? []}
              isGlobal={isGlobal}
              pendingActionId={pendingDispatchActionId}
              actionError={visibleDispatchError}
              onApprove={dispatch => void handleDispatchDecision(dispatch, 'approve')}
              onReject={dispatch => void handleDispatchDecision(dispatch, 'reject')}
            />
          </div>
        </div>
      </div>

      {/* Projects sidebar — bottom left */}
      <ProjectSidebar
        projects={projects ?? []}
        selectedId={effectiveProject}
        onSelect={handleProjectSelect}
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
              onSelect={handleTaskSelect}
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
          key={`${effectiveProject}:${selectedTaskId}`}
          projectId={effectiveProject}
          taskId={selectedTaskId}
          onSelectTask={handleTaskSelect}
          onSelectMessage={handleMessageSelect}
          onClose={() => setSelectedTaskId(null)}
        />
      )}

      {selectedMessage && (
        <MessageDetail
          key={`${selectedMessage.project_id}:${selectedMessage.id}`}
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
