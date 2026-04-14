import { useState, useEffect, useCallback, useRef } from 'react';

export function usePolling<T>(
  fetcher: () => Promise<T>,
  intervalMs: number,
): { data: T | null; loading: boolean; error: Error | null; refresh: () => void } {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<Error | null>(null);
  const fetcherRef = useRef(fetcher);
  const latestRequestIdRef = useRef(0);
  fetcherRef.current = fetcher;

  const doFetch = useCallback(async () => {
    const requestId = ++latestRequestIdRef.current;
    setLoading(true);

    try {
      const result = await fetcherRef.current();
      if (requestId !== latestRequestIdRef.current) return;

      setData(result);
      setError(null);
    } catch (e) {
      if (requestId !== latestRequestIdRef.current) return;

      setError(e instanceof Error ? e : new Error(String(e)));
    } finally {
      if (requestId !== latestRequestIdRef.current) return;

      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void doFetch();
  }, [doFetch, fetcher]);

  useEffect(() => {
    const id = setInterval(doFetch, intervalMs);
    return () => clearInterval(id);
  }, [doFetch, intervalMs]);

  return { data, loading, error, refresh: doFetch };
}
