export const environment = {
  production: false,
  // All services use relative URLs (/api/*, /hubs/*) proxied via proxy.conf.json.
  // Dev:  proxy.conf.json → http://localhost:5000  (API via dotnet run or Docker)
  // Prod: nginx rewrites /api/* and /hubs/* to the backend container directly.
  apiUrl: 'http://localhost:5000',
};
