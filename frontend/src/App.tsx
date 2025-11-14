import { useQuery } from "@tanstack/react-query";
import axios from "axios";
import { useMemo } from "react";

interface HealthResponse {
  status: string;
  timestamp: string;
}

const apiBaseUrl = (import.meta.env.VITE_API_URL as string | undefined)?.replace(/\/$/, "") ?? "";
const healthEndpoint = apiBaseUrl ? `${apiBaseUrl}/api/health` : "/api/health";

const fetchHealth = async (): Promise<HealthResponse> => {
  const { data } = await axios.get<HealthResponse>(healthEndpoint);
  return data;
};

function App() {
  const { data, error, isLoading, refetch, isFetching } = useQuery({
    queryKey: ["health"],
    queryFn: fetchHealth,
    refetchOnWindowFocus: false
  });

  const formattedTimestamp = useMemo(() => {
    if (!data?.timestamp) {
      return "";
    }

    try {
      return new Date(data.timestamp).toLocaleString();
    } catch {
      return data.timestamp;
    }
  }, [data?.timestamp]);

  return (
    <main className="app-shell">
      <header>
        <h1>Strim</h1>
        <p className="subtitle">Playlist cleaner foundation</p>
      </header>

      <section className="card">
        <h2>Backend Health</h2>
        {isLoading || isFetching ? <p>Checking...</p> : null}
        {error ? (
          <p className="error">Unable to contact the backend. Start the API to continue.</p>
        ) : null}
        {data ? (
          <dl className="health-grid">
            <div>
              <dt>Status</dt>
              <dd>{data.status}</dd>
            </div>
            <div>
              <dt>Timestamp</dt>
              <dd>{formattedTimestamp}</dd>
            </div>
          </dl>
        ) : null}
        <button type="button" onClick={() => refetch()}>
          Refresh
        </button>
      </section>
    </main>
  );
}

export default App;
