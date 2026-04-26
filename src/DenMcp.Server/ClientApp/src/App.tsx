import { useState, useCallback, useMemo } from 'react';
import type { AgentStreamEntry, DispatchEntry, Document, DocumentSummary, Message, MessageIntent, SubagentRunSummary } from './api/types';
import {
  getDispatch,
  listProjects,
  listTasks,
  getMessageFeed,
  getThread,
  listAgentStream,
  listSubagentRuns,
  subagentRunEventsUrl,
  listDocuments,
  listActiveAgents,
  listDispatches,
  approveDispatch,
  rejectDispatch,
} from './api/client';
import { usePolling } from './hooks/usePolling';
import { useEventSourceRefresh } from './hooks/useEventSourceRefresh';
import { ProjectSidebar } from './components/ProjectSidebar';
import { TaskTree } from './components/TaskTree';
import { TaskDetail } from './components/TaskDetail';
import { FilterBar } from './components/FilterBar';
import { MessageFeed } from './components/MessageFeed';
import { MessageDetail } from './components/MessageDetail';
import { AgentStreamFeed } from './components/AgentStreamFeed';
import { AgentStreamDetail } from './components/AgentStreamDetail';
import { SubagentRunPanel } from './components/SubagentRunPanel';
import { SubagentRunDetail } from './components/SubagentRunDetail';
import { DocumentList } from './components/DocumentList';
import { DocumentDetail } from './components/DocumentDetail';
import { AgentBar } from './components/AgentBar';
import { DispatchPanel } from './components/DispatchPanel';
import { DispatchDetail } from './components/DispatchDetail';
import { MESSAGE_INTENT_OPTIONS, messageIntentLabel } from './messageIntents';
import type { SubagentRunFilter } from './subagentRuns';

export default function App() {
  const [selectedProject, setSelectedProject] = useState<string | null>(null);
  const [selectedTaskId, setSelectedTaskId] = useState<number | null>(null);
  const [selectedTaskProjectId, setSelectedTaskProjectId] = useState<string | null>(null);
  const [selectedMessage, setSelectedMessage] = useState<Message | null>(null);
  const [selectedStreamEntry, setSelectedStreamEntry] = useState<AgentStreamEntry | null>(null);
  const [selectedSubagentRun, setSelectedSubagentRun] = useState<SubagentRunSummary | null>(null);
  const [selectedDispatch, setSelectedDispatch] = useState<DispatchEntry | null>(null);
  const [selectedDoc, setSelectedDoc] = useState<DocumentSummary | null>(null);
  const [viewMode, setViewMode] = useState<'tasks' | 'documents'>('tasks');
  const [feedMode, setFeedMode] = useState<'stream' | 'messages'>('stream');
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [messageIntentFilter, setMessageIntentFilter] = useState<MessageIntent | ''>('');
  const [streamKindFilter, setStreamKindFilter] = useState<'ops' | 'message'>('ops');
  const [streamEventFilter, setStreamEventFilter] = useState('');
  const [streamProjectFilter, setStreamProjectFilter] = useState('');
  const [streamSenderFilter, setStreamSenderFilter] = useState('');
  const [streamRecipientFilter, setStreamRecipientFilter] = useState('');
  const [streamTaskFilter, setStreamTaskFilter] = useState('');
  const [subagentRunFilter, setSubagentRunFilter] = useState<SubagentRunFilter>('all');
  const [subagentRunLimit, setSubagentRunLimit] = useState(8);
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

  const parsedStreamTaskId = useMemo(() => {
    const trimmed = streamTaskFilter.trim();
    return /^\d+$/.test(trimmed) ? Number(trimmed) : undefined;
  }, [streamTaskFilter]);

  const fetchAgentStream = useCallback(
    () => effectiveProject
      ? listAgentStream({
        projectId: isGlobal ? (streamProjectFilter.trim() || undefined) : effectiveProject,
        taskId: parsedStreamTaskId,
        streamKind: streamKindFilter,
        eventType: streamEventFilter || undefined,
        sender: streamSenderFilter.trim() || undefined,
        limit: 60,
      })
      : Promise.resolve([]),
    [
      effectiveProject,
      isGlobal,
      parsedStreamTaskId,
      streamEventFilter,
      streamKindFilter,
      streamProjectFilter,
      streamSenderFilter,
    ],
  );
  const { data: agentStream } = usePolling(fetchAgentStream, 5000);

  const fetchSubagentRuns = useCallback(
    () => effectiveProject
      ? listSubagentRuns({
        projectId: isGlobal ? (streamProjectFilter.trim() || undefined) : effectiveProject,
        taskId: parsedStreamTaskId,
        state: subagentRunFilter === 'all' ? undefined : subagentRunFilter,
        limit: subagentRunLimit,
      })
      : Promise.resolve([]),
    [effectiveProject, isGlobal, parsedStreamTaskId, streamProjectFilter, subagentRunFilter, subagentRunLimit],
  );
  const {
    data: subagentRuns,
    loading: subagentRunsLoading,
    error: subagentRunsError,
    refresh: refreshSubagentRuns,
  } = usePolling(fetchSubagentRuns, 2000);
  const subagentRunEvents = useMemo(
    () => effectiveProject
      ? subagentRunEventsUrl({
        projectId: isGlobal ? (streamProjectFilter.trim() || undefined) : effectiveProject,
        taskId: parsedStreamTaskId,
      })
      : null,
    [effectiveProject, isGlobal, parsedStreamTaskId, streamProjectFilter],
  );
  useEventSourceRefresh(subagentRunEvents, 'subagent_run_updated', refreshSubagentRuns);

  const fetchDocs = useCallback(
    () => effectiveProject
      ? listDocuments(isGlobal ? undefined : effectiveProject)
      : Promise.resolve([]),
    [effectiveProject, isGlobal],
  );
  const { data: documents, refresh: refreshDocs } = usePolling(fetchDocs, 5000);

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
  const filteredAgentStream = useMemo(() => {
    const recipientFilter = streamRecipientFilter.trim().toLowerCase();
    if (!recipientFilter) {
      return agentStream ?? [];
    }

    return (agentStream ?? []).filter(entry => {
      const recipients = [
        entry.recipient_agent,
        entry.recipient_role,
        entry.recipient_instance_id,
      ]
        .filter((value): value is string => Boolean(value))
        .map(value => value.toLowerCase());
      return recipients.some(value => value.includes(recipientFilter));
    });
  }, [agentStream, streamRecipientFilter]);
  const streamEventOptions = useMemo(() => {
    const options = new Set((agentStream ?? []).map(entry => entry.event_type));
    if (streamEventFilter) {
      options.add(streamEventFilter);
    }
    return Array.from(options).sort((left, right) => left.localeCompare(right));
  }, [agentStream, streamEventFilter]);

  const taskCount = tasks?.length ?? 0;
  const filterLabel = statusFilter ? ` [${statusFilter}]` : '';
  const messageFilterLabel = messageIntentFilter ? ` [${messageIntentFilter}]` : '';
  const sortLabel = sortMode !== 'priority' ? ` \u2195${sortMode}` : '';

  const handleProjectSelect = useCallback((id: string) => {
    setSelectedProject(id);
    setSelectedTaskId(null);
    setSelectedTaskProjectId(null);
    setSelectedMessage(null);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, []);

  const handleTaskSelect = useCallback((taskId: number, projectId?: string | null) => {
    const targetProjectId = projectId?.trim() || effectiveProject;
    if (targetProjectId && targetProjectId !== selectedProject) {
      setSelectedProject(targetProjectId);
    }
    setSelectedTaskId(taskId);
    setSelectedTaskProjectId(targetProjectId ?? null);
    setSelectedMessage(null);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
    setViewMode('tasks');
  }, [effectiveProject, selectedProject]);

  const handleMessageSelect = useCallback((message: Message) => {
    setSelectedMessage(message);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, []);

  const handleStreamSelect = useCallback((entry: AgentStreamEntry) => {
    setSelectedStreamEntry(entry);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, []);

  const handleSubagentRunSelect = useCallback((run: SubagentRunSummary) => {
    setSelectedSubagentRun(run);
    setSelectedStreamEntry(null);
    setSelectedDispatch(null);
    setSelectedDoc(null);
  }, []);

  const handleDispatchSelect = useCallback(async (dispatchId: number) => {
    try {
      const dispatch = await getDispatch(dispatchId);
      setSelectedDispatch(dispatch);
      setSelectedStreamEntry(null);
      setSelectedSubagentRun(null);
      setSelectedDoc(null);
    } catch (error) {
      console.error('Failed to load dispatch detail', error);
    }
  }, []);

  const handleDocumentSaved = useCallback((doc: Document) => {
    setSelectedDoc({
      id: doc.id,
      project_id: doc.project_id,
      slug: doc.slug,
      title: doc.title,
      doc_type: doc.doc_type,
      tags: doc.tags,
      updated_at: doc.updated_at,
    });
    refreshDocs();
  }, [refreshDocs]);

  const handleStreamThreadOpen = useCallback(async (entry: AgentStreamEntry) => {
    if (!entry.project_id || entry.thread_id == null) {
      return;
    }

    try {
      const thread = await getThread(entry.project_id, entry.thread_id);
      setSelectedMessage(thread.root);
      setSelectedStreamEntry(null);
      setSelectedSubagentRun(null);
      setSelectedDispatch(null);
      setSelectedDoc(null);
    } catch (error) {
      console.error('Failed to load thread detail', error);
    }
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
  const feedCount = feedMode === 'stream'
    ? filteredAgentStream.length
    : (messages?.length ?? 0);

  return (
    <div className="dashboard">
      {/* Feed — top, full width */}
      <div className="panel panel-messages">
        <div className="panel-header">
          {feedMode === 'stream' ? 'Agent Stream' : 'Messages'}
          {effectiveProject && (
            <span className="count">
              ({feedCount}{feedMode === 'messages' ? messageFilterLabel : ''})
            </span>
          )}
        </div>
        <div className="feed-toolbar">
          <div className="feed-toggle">
            <button
              className={feedMode === 'stream' ? 'active' : ''}
              onClick={() => setFeedMode('stream')}
            >
              Stream
            </button>
            <button
              className={feedMode === 'messages' ? 'active' : ''}
              onClick={() => setFeedMode('messages')}
            >
              Messages
            </button>
          </div>

          {feedMode === 'stream' ? (
            <>
              <label className="panel-filter-label" htmlFor="stream-kind-filter">Kind</label>
              <select
                id="stream-kind-filter"
                className="panel-filter-select"
                value={streamKindFilter}
                onChange={e => setStreamKindFilter(e.target.value as 'ops' | 'message')}
              >
                <option value="ops">Ops</option>
                <option value="message">Messages</option>
              </select>

              <label className="panel-filter-label" htmlFor="stream-event-filter">Event</label>
              <select
                id="stream-event-filter"
                className="panel-filter-select"
                value={streamEventFilter}
                onChange={e => setStreamEventFilter(e.target.value)}
              >
                <option value="">All</option>
                {streamEventOptions.map(eventType => (
                  <option key={eventType} value={eventType}>{eventType}</option>
                ))}
              </select>

              {isGlobal && (
                <input
                  className="feed-text-filter"
                  value={streamProjectFilter}
                  onChange={e => setStreamProjectFilter(e.target.value)}
                  placeholder="Project"
                />
              )}

              <input
                className="feed-text-filter"
                value={streamSenderFilter}
                onChange={e => setStreamSenderFilter(e.target.value)}
                placeholder="Sender"
              />

              <input
                className="feed-text-filter"
                value={streamRecipientFilter}
                onChange={e => setStreamRecipientFilter(e.target.value)}
                placeholder="Recipient"
              />

              <input
                className="feed-text-filter feed-text-filter-short"
                value={streamTaskFilter}
                onChange={e => setStreamTaskFilter(e.target.value)}
                placeholder="Task #"
              />
            </>
          ) : (
            <>
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
            </>
          )}
        </div>
        <div className="panel-body">
          {feedMode === 'stream' ? (
            <AgentStreamFeed
              entries={filteredAgentStream}
              isGlobal={isGlobal}
              onSelect={handleStreamSelect}
              onOpenTask={handleTaskSelect}
              onOpenThread={entry => void handleStreamThreadOpen(entry)}
              onOpenDispatch={dispatchId => void handleDispatchSelect(dispatchId)}
            />
          ) : (
            <MessageFeed
              messages={messages ?? []}
              isGlobal={isGlobal}
              onSelect={handleMessageSelect}
            />
          )}
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

        <div className="panel panel-subagents">
          <div className="panel-header">
            Sub-agent Runs <span className="count">({subagentRuns?.length ?? 0})</span>
          </div>
          <div className="panel-body">
            <SubagentRunPanel
              runs={subagentRuns ?? []}
              totalCount={subagentRuns?.length ?? 0}
              isGlobal={isGlobal}
              filter={subagentRunFilter}
              limit={subagentRunLimit}
              loading={subagentRunsLoading}
              error={subagentRunsError}
              onFilterChange={setSubagentRunFilter}
              onLimitChange={setSubagentRunLimit}
              onRefresh={refreshSubagentRuns}
              onSelectRun={handleSubagentRunSelect}
              onOpenTask={handleTaskSelect}
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
          key={`${selectedTaskProjectId ?? effectiveProject}:${selectedTaskId}`}
          projectId={selectedTaskProjectId ?? effectiveProject}
          taskId={selectedTaskId}
          onSelectTask={handleTaskSelect}
          onSelectMessage={handleMessageSelect}
          onClose={() => {
            setSelectedTaskId(null);
            setSelectedTaskProjectId(null);
          }}
        />
      )}

      {selectedMessage && (
        <MessageDetail
          key={`${selectedMessage.project_id}:${selectedMessage.id}`}
          message={selectedMessage}
          onClose={() => setSelectedMessage(null)}
        />
      )}

      {selectedStreamEntry && (
        <AgentStreamDetail
          key={selectedStreamEntry.id}
          entry={selectedStreamEntry}
          onClose={() => setSelectedStreamEntry(null)}
          onOpenTask={handleTaskSelect}
          onOpenThread={entry => void handleStreamThreadOpen(entry)}
          onOpenDispatch={dispatchId => void handleDispatchSelect(dispatchId)}
        />
      )}

      {selectedSubagentRun && (
        <SubagentRunDetail
          key={selectedSubagentRun.run_id}
          run={selectedSubagentRun}
          onClose={() => setSelectedSubagentRun(null)}
          onOpenTask={handleTaskSelect}
          onOpenEntry={handleStreamSelect}
        />
      )}

      {selectedDispatch && (
        <DispatchDetail
          dispatch={selectedDispatch}
          onClose={() => setSelectedDispatch(null)}
          onOpenTask={handleTaskSelect}
        />
      )}

      {selectedDoc && (
        <DocumentDetail
          summary={selectedDoc}
          onClose={() => setSelectedDoc(null)}
          onSaved={handleDocumentSaved}
        />
      )}
    </div>
  );
}
