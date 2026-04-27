import { useState, useCallback, useMemo } from 'react';
import type { AgentStreamEntry, DispatchEntry, Document, DocumentSummary, Message, MessageIntent, SubagentRunSummary } from './api/types';
import {
  getDispatch,
  listProjects,
  listTasks,
  getMessage,
  getMessageFeed,
  getThread,
  listAgentStream,
  listSubagentRuns,
  subagentRunEventsUrl,
  getSubagentRun,
  listDocuments,
  listActiveAgents,
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
import { ThoughtFeed } from './components/ThoughtFeed';
import { SubagentRunPanel } from './components/SubagentRunPanel';
import { SubagentRunDetail } from './components/SubagentRunDetail';
import { DocumentList } from './components/DocumentList';
import { DocumentDetail } from './components/DocumentDetail';
import { LibrarianView } from './components/LibrarianView';
import { AgentBar } from './components/AgentBar';
import { DispatchDetail } from './components/DispatchDetail';
import { MESSAGE_INTENT_OPTIONS, messageIntentLabel } from './messageIntents';
import type { SubagentRunFilter } from './subagentRuns';
import {
  filterThoughtItems,
  hasRawReasoningPreview,
  sortThoughtItems,
  thoughtItemFromStreamEntry,
  thoughtItemsFromSubagentRunDetail,
} from './thoughts';

export default function App() {
  const [selectedProject, setSelectedProject] = useState<string | null>(null);
  const [selectedTaskId, setSelectedTaskId] = useState<number | null>(null);
  const [selectedTaskProjectId, setSelectedTaskProjectId] = useState<string | null>(null);
  const [selectedMessage, setSelectedMessage] = useState<Message | null>(null);
  const [selectedStreamEntry, setSelectedStreamEntry] = useState<AgentStreamEntry | null>(null);
  const [selectedSubagentRun, setSelectedSubagentRun] = useState<SubagentRunSummary | null>(null);
  const [selectedDispatch, setSelectedDispatch] = useState<DispatchEntry | null>(null);
  const [selectedDoc, setSelectedDoc] = useState<DocumentSummary | null>(null);
  const [viewMode, setViewMode] = useState<'tasks' | 'documents' | 'librarian'>('tasks');
  const [feedMode, setFeedMode] = useState<'stream' | 'messages' | 'thoughts'>('stream');
  const [statusFilter, setStatusFilter] = useState<string | null>(null);
  const [messageIntentFilter, setMessageIntentFilter] = useState<MessageIntent | ''>('');
  const [streamKindFilter, setStreamKindFilter] = useState<'ops' | 'message'>('ops');
  const [streamEventFilter, setStreamEventFilter] = useState('');
  const [streamProjectFilter, setStreamProjectFilter] = useState('');
  const [streamSenderFilter, setStreamSenderFilter] = useState('');
  const [streamRecipientFilter, setStreamRecipientFilter] = useState('');
  const [streamTaskFilter, setStreamTaskFilter] = useState('');
  const [thoughtProjectFilter, setThoughtProjectFilter] = useState('');
  const [thoughtTaskFilter, setThoughtTaskFilter] = useState('');
  const [thoughtAgentFilter, setThoughtAgentFilter] = useState('');
  const [thoughtRoleFilter, setThoughtRoleFilter] = useState('');
  const [thoughtRawMode, setThoughtRawMode] = useState(false);
  const [subagentRunFilter, setSubagentRunFilter] = useState<SubagentRunFilter>('all');
  const [subagentRunLimit, setSubagentRunLimit] = useState(8);
  const [sortMode, setSortMode] = useState('priority');

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

  const parsedThoughtTaskId = useMemo(() => {
    const trimmed = thoughtTaskFilter.trim();
    return /^\d+$/.test(trimmed) ? Number(trimmed) : undefined;
  }, [thoughtTaskFilter]);

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

  const fetchThoughts = useCallback(async () => {
    if (!effectiveProject || feedMode !== 'thoughts') return [];

    const projectFilter = isGlobal ? (thoughtProjectFilter.trim() || undefined) : effectiveProject;
    const [streamEntries, runs] = await Promise.all([
      listAgentStream({
        projectId: projectFilter,
        taskId: parsedThoughtTaskId,
        streamKind: 'ops',
        sender: thoughtAgentFilter.trim() || undefined,
        limit: 100,
      }),
      listSubagentRuns({
        projectId: projectFilter,
        taskId: parsedThoughtTaskId,
        limit: 10,
      }),
    ]);

    const runDetails = await Promise.allSettled(runs.slice(0, 8).map(run => getSubagentRun(run.run_id, {
      projectId: run.project_id ?? projectFilter,
      taskId: run.task_id ?? undefined,
    })));

    const streamThoughts = streamEntries
      .map(thoughtItemFromStreamEntry)
      .filter(item => item !== null);
    const runThoughts = runDetails.flatMap(result => result.status === 'fulfilled'
      ? thoughtItemsFromSubagentRunDetail(result.value)
      : []);

    return filterThoughtItems(sortThoughtItems([...streamThoughts, ...runThoughts]), {
      project: isGlobal ? thoughtProjectFilter : undefined,
      taskId: parsedThoughtTaskId,
      agent: thoughtAgentFilter,
      role: thoughtRoleFilter,
    });
  }, [
    effectiveProject,
    feedMode,
    isGlobal,
    parsedThoughtTaskId,
    thoughtAgentFilter,
    thoughtProjectFilter,
    thoughtRoleFilter,
  ]);
  const {
    data: thoughts,
    loading: thoughtsLoading,
    error: thoughtsError,
    refresh: refreshThoughts,
  } = usePolling(fetchThoughts, 4000);
  const thoughtSubagentRunEvents = useMemo(
    () => effectiveProject
      ? subagentRunEventsUrl({
        projectId: isGlobal ? (thoughtProjectFilter.trim() || undefined) : effectiveProject,
        taskId: parsedThoughtTaskId,
      })
      : null,
    [effectiveProject, isGlobal, parsedThoughtTaskId, thoughtProjectFilter],
  );
  useEventSourceRefresh(thoughtSubagentRunEvents, 'subagent_run_updated', refreshThoughts);

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
  const rawReasoningAvailable = useMemo(() => hasRawReasoningPreview(thoughts ?? []), [thoughts]);

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

  const handleDocumentSelect = useCallback((doc: DocumentSummary) => {
    if (doc.project_id && doc.project_id !== selectedProject) {
      setSelectedProject(doc.project_id);
    }
    setSelectedDoc(doc);
    setSelectedTaskId(null);
    setSelectedTaskProjectId(null);
    setSelectedMessage(null);
    setSelectedStreamEntry(null);
    setSelectedSubagentRun(null);
    setSelectedDispatch(null);
  }, [selectedProject]);

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

  const handleMessageOpen = useCallback(async (projectId: string, messageId: number) => {
    try {
      const message = await getMessage(projectId, messageId);
      if (!message) return;
      setSelectedMessage(message);
      setSelectedStreamEntry(null);
      setSelectedSubagentRun(null);
      setSelectedDispatch(null);
      setSelectedDoc(null);
    } catch (error) {
      console.error('Failed to load message detail', error);
    }
  }, []);

  const handleThreadOpen = useCallback(async (projectId: string, threadId: number) => {
    try {
      const thread = await getThread(projectId, threadId);
      setSelectedMessage(thread.root);
      setSelectedStreamEntry(null);
      setSelectedSubagentRun(null);
      setSelectedDispatch(null);
      setSelectedDoc(null);
    } catch (error) {
      console.error('Failed to load thread detail', error);
    }
  }, []);

  const handleStreamThreadOpen = useCallback(async (entry: AgentStreamEntry) => {
    if (!entry.project_id || entry.thread_id == null) {
      return;
    }

    await handleThreadOpen(entry.project_id, entry.thread_id);
  }, [handleThreadOpen]);

  const feedCount = feedMode === 'stream'
    ? filteredAgentStream.length
    : feedMode === 'thoughts'
      ? (thoughts?.length ?? 0)
      : (messages?.length ?? 0);
  const feedTitle = feedMode === 'stream'
    ? 'Agent Stream'
    : feedMode === 'thoughts'
      ? 'Thoughts'
      : 'Messages';

  return (
    <div className="dashboard">
      {/* Feed — top, full width */}
      <div className="panel panel-messages">
        <div className="panel-header">
          {feedTitle}
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
            <button
              className={feedMode === 'thoughts' ? 'active' : ''}
              onClick={() => setFeedMode('thoughts')}
            >
              Thoughts
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
          ) : feedMode === 'messages' ? (
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
          ) : (
            <>
              {isGlobal && (
                <input
                  className="feed-text-filter"
                  value={thoughtProjectFilter}
                  onChange={e => setThoughtProjectFilter(e.target.value)}
                  placeholder="Project"
                />
              )}

              <input
                className="feed-text-filter"
                value={thoughtAgentFilter}
                onChange={e => setThoughtAgentFilter(e.target.value)}
                placeholder="Agent"
              />

              <input
                className="feed-text-filter"
                value={thoughtRoleFilter}
                onChange={e => setThoughtRoleFilter(e.target.value)}
                placeholder="Role"
              />

              <input
                className="feed-text-filter feed-text-filter-short"
                value={thoughtTaskFilter}
                onChange={e => setThoughtTaskFilter(e.target.value)}
                placeholder="Task #"
              />

              <label
                className={`thought-raw-toggle${rawReasoningAvailable ? '' : ' thought-raw-toggle-disabled'}`}
                title={rawReasoningAvailable ? 'Show bounded local raw reasoning previews' : 'No local raw reasoning previews in this feed'}
              >
                <input
                  type="checkbox"
                  checked={thoughtRawMode && rawReasoningAvailable}
                  disabled={!rawReasoningAvailable}
                  onChange={e => setThoughtRawMode(e.target.checked)}
                />
                Raw local
              </label>
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
          ) : feedMode === 'messages' ? (
            <MessageFeed
              messages={messages ?? []}
              isGlobal={isGlobal}
              onSelect={handleMessageSelect}
            />
          ) : (
            <ThoughtFeed
              items={thoughts ?? []}
              isGlobal={isGlobal}
              loading={thoughtsLoading}
              error={thoughtsError}
              showRawReasoning={thoughtRawMode && rawReasoningAvailable}
              rawReasoningAvailable={rawReasoningAvailable}
              onOpenTask={handleTaskSelect}
              onOpenRun={handleSubagentRunSelect}
              onOpenStream={handleStreamSelect}
            />
          )}
        </div>
      </div>

      {/* Middle row — agents plus active sub-agent work */}
      <div className="panel-middle-grid">
        <div className="panel panel-agents">
          <div className="panel-header">
            Agents <span className="count">({agents?.length ?? 0})</span>
          </div>
          <div className="panel-body panel-body-agents">
            <AgentBar agents={agents ?? []} isGlobal={isGlobal} />
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
            : viewMode === 'documents'
              ? <>Documents {effectiveProject && <span className="count">({sortedDocs.length})</span>}</>
              : <>Librarian</>
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
          ) : viewMode === 'documents' ? (
            <DocumentList
              documents={sortedDocs}
              projectId={effectiveProject}
              isGlobal={isGlobal}
              onSelect={handleDocumentSelect}
            />
          ) : (
            <LibrarianView
              projects={projects ?? []}
              currentProjectId={effectiveProject}
              onOpenTask={handleTaskSelect}
              onOpenDocument={handleDocumentSelect}
              onOpenMessage={(projectId, messageId) => void handleMessageOpen(projectId, messageId)}
              onOpenThread={(projectId, threadId) => void handleThreadOpen(projectId, threadId)}
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
          onSelectRun={handleSubagentRunSelect}
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
