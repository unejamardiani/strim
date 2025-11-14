import { FormEvent, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import axios from "axios";

interface HealthResponse {
  status: string;
  timestamp: string;
}

interface PlaylistSummary {
  id: string;
  name: string;
  source: string;
  sourceType: string;
  channelCount: number;
  createdAt: string;
}

interface PlaylistChannelPreview {
  id: string;
  name: string;
  url: string;
  groupTitle?: string | null;
  tvgId?: string | null;
  tvgName?: string | null;
  tvgLogo?: string | null;
  sortOrder: number;
}

interface PlaylistParseResponse {
  playlist: PlaylistSummary;
  channels: PlaylistChannelPreview[];
}

interface PlaylistParseRequest {
  url?: string;
  filePath?: string;
  name?: string;
}

const apiBaseUrl = (import.meta.env.VITE_API_URL as string | undefined)?.replace(/\/$/, "") ?? "";
const healthEndpoint = apiBaseUrl ? `${apiBaseUrl}/api/health` : "/api/health";
const playlistsEndpoint = apiBaseUrl ? `${apiBaseUrl}/api/playlists` : "/api/playlists";
const parseEndpoint = playlistsEndpoint + "/parse";

const fetchHealth = async (): Promise<HealthResponse> => {
  const { data } = await axios.get<HealthResponse>(healthEndpoint);
  return data;
};

const fetchPlaylists = async (): Promise<PlaylistSummary[]> => {
  const { data } = await axios.get<PlaylistSummary[]>(playlistsEndpoint);
  return data;
};

const parsePlaylist = async (payload: PlaylistParseRequest): Promise<PlaylistParseResponse> => {
  const { data } = await axios.post<PlaylistParseResponse>(parseEndpoint, payload);
  return data;
};

function App() {
  const [url, setUrl] = useState("");
  const [filePath, setFilePath] = useState("");
  const [name, setName] = useState("");
  const [formError, setFormError] = useState<string | null>(null);
  const [lastResult, setLastResult] = useState<PlaylistParseResponse | null>(null);

  const queryClient = useQueryClient();

  const healthQuery = useQuery({
    queryKey: ["health"],
    queryFn: fetchHealth,
    refetchOnWindowFocus: false
  });

  const playlistsQuery = useQuery({
    queryKey: ["playlists"],
    queryFn: fetchPlaylists,
    refetchOnWindowFocus: false
  });

  const parseMutation = useMutation({
    mutationFn: parsePlaylist,
    onSuccess: async (data) => {
      setLastResult(data);
      await queryClient.invalidateQueries({ queryKey: ["playlists"] });
    }
  });

  const formattedTimestamp = useMemo(() => {
    if (!healthQuery.data?.timestamp) {
      return "";
    }

    try {
      return new Date(healthQuery.data.timestamp).toLocaleString();
    } catch {
      return healthQuery.data.timestamp;
    }
  }, [healthQuery.data?.timestamp]);

  const handleSubmit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    setFormError(null);

    const trimmedUrl = url.trim();
    const trimmedFile = filePath.trim();

    if (!trimmedUrl && !trimmedFile) {
      setFormError("Provide either a playlist URL or a file path accessible to the API container.");
      return;
    }

    if (trimmedUrl && trimmedFile) {
      setFormError("Only one of URL or file path can be provided at a time.");
      return;
    }

    const payload: PlaylistParseRequest = {
      name: name.trim() || undefined,
      ...(trimmedUrl ? { url: trimmedUrl } : { filePath: trimmedFile })
    };

    try {
      await parseMutation.mutateAsync(payload);
    } catch (error) {
      if (axios.isAxiosError<{ error?: string }>(error)) {
        const responseMessage = error.response?.data?.error;
        setFormError(responseMessage ?? "Playlist parsing failed. Check the backend logs for details.");
      } else {
        setFormError("Playlist parsing failed. Check the backend logs for details.");
      }
    }
  };

  const isHealthLoading = healthQuery.isLoading || healthQuery.isFetching;
  const playlists = playlistsQuery.data ?? [];

  return (
    <main className="app-shell">
      <header>
        <h1>Strim</h1>
        <p className="subtitle">Playlist ingestion and normalization</p>
      </header>

      <section className="card">
        <h2>Backend Health</h2>
        {isHealthLoading ? <p>Checking...</p> : null}
        {healthQuery.error ? (
          <p className="error">Unable to contact the backend. Start the API to continue.</p>
        ) : null}
        {healthQuery.data ? (
          <dl className="health-grid">
            <div>
              <dt>Status</dt>
              <dd>{healthQuery.data.status}</dd>
            </div>
            <div>
              <dt>Timestamp</dt>
              <dd>{formattedTimestamp}</dd>
            </div>
          </dl>
        ) : null}
        <button type="button" onClick={() => healthQuery.refetch()}>
          Refresh
        </button>
      </section>

      <section className="card">
        <h2>Parse Playlist</h2>
        <p className="helper-text">
          Provide a source URL or a file path mounted into the API container. Successful parses are stored for future rule runs.
        </p>
        <form className="form-grid" onSubmit={handleSubmit}>
          <label>
            <span>Playlist URL</span>
            <input
              type="url"
              placeholder="https://provider.example/playlist.m3u8"
              value={url}
              onChange={(event) => setUrl(event.target.value)}
              disabled={parseMutation.isPending}
            />
          </label>
          <label>
            <span>or File Path</span>
            <input
              type="text"
              placeholder="/data/raw-playlist.m3u"
              value={filePath}
              onChange={(event) => setFilePath(event.target.value)}
              disabled={parseMutation.isPending}
            />
          </label>
          <label>
            <span>Friendly Name</span>
            <input
              type="text"
              placeholder="My Provider"
              value={name}
              onChange={(event) => setName(event.target.value)}
              disabled={parseMutation.isPending}
            />
          </label>
          {formError ? <p className="error" role="alert">{formError}</p> : null}
          <button type="submit" disabled={parseMutation.isPending}>
            {parseMutation.isPending ? "Parsing..." : "Parse playlist"}
          </button>
        </form>
        {lastResult ? (
          <div className="parse-results">
            <h3>Latest Parse Preview</h3>
            <p>
              Stored <strong>{lastResult.playlist.channelCount}</strong> channels for <strong>{lastResult.playlist.name}</strong>.
              {lastResult.channels.length > 0 ? ` Showing the first ${lastResult.channels.length} entries.` : ""}
            </p>
              {lastResult.channels.length > 0 ? (
                <ul>
                  {lastResult.channels.map((channel) => (
                  <li key={channel.id} title={channel.url}>
                    <span className="channel-name">{channel.name}</span>
                    <span className="channel-meta">#{channel.sortOrder + 1}</span>
                    {channel.groupTitle ? <span className="channel-meta">Group: {channel.groupTitle}</span> : null}
                    <span className="channel-meta channel-url">{channel.url}</span>
                  </li>
                  ))}
                </ul>
              ) : (
                <p className="helper-text">No channel preview available for this playlist.</p>
            )}
          </div>
        ) : null}
      </section>

      <section className="card">
        <h2>Stored Playlists</h2>
        {playlistsQuery.isLoading || playlistsQuery.isFetching ? <p>Loading playlists...</p> : null}
        {playlistsQuery.error ? <p className="error">Unable to load playlists.</p> : null}
        {!playlistsQuery.isLoading && playlists.length === 0 ? <p>No playlists ingested yet.</p> : null}
        {playlists.length > 0 ? (
          <div className="table-scroll">
            <table className="playlist-table">
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Source</th>
                  <th>Channels</th>
                  <th>Imported</th>
                </tr>
              </thead>
              <tbody>
                {playlists.map((playlist) => (
                  <tr key={playlist.id}>
                    <td>{playlist.name}</td>
                    <td>
                      <span className="source-type">{playlist.sourceType}</span>
                      <span className="source-value" title={playlist.source}>
                        {playlist.source}
                      </span>
                    </td>
                    <td>{playlist.channelCount}</td>
                    <td>{new Date(playlist.createdAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : null}
      </section>
    </main>
  );
}

export default App;
