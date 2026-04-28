import { Component, ErrorInfo, ReactNode } from 'react';

interface Props {
  children: ReactNode;
}

interface State {
  error: Error | null;
  errorInfo: ErrorInfo | null;
}

export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null, errorInfo: null };

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    this.setState({ error, errorInfo });
    console.error('Den desktop UI error boundary caught an error', error, errorInfo);
  }

  private reset = () => {
    this.setState({ error: null, errorInfo: null });
  };

  render() {
    if (!this.state.error) {
      return this.props.children;
    }

    return (
      <main className="app-shell">
        <section className="panel error-boundary-panel">
          <p className="eyebrow">Desktop UI degraded</p>
          <h1>Something in the operator surface crashed.</h1>
          <p className="muted">
            The local Tauri shell is still running. You can try to reset the React shell or reload the window without treating this as a Den outage.
          </p>
          <p className="error-note">{this.state.error.message}</p>
          {this.state.errorInfo?.componentStack && <pre>{this.state.errorInfo.componentStack}</pre>}
          <div className="button-row boundary-actions">
            <button type="button" onClick={this.reset}>Reset UI</button>
            <button type="button" className="secondary" onClick={() => window.location.reload()}>Reload window</button>
          </div>
        </section>
      </main>
    );
  }
}
