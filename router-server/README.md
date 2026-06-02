# Router Server

A high-performance Go-based router server for the Remote Desktop Automation application.

## Features

- **Fast Connection Routing**: Written in Go for maximum performance
- **Unique Code Generation**: Generates 6-character alphanumeric connection codes
- **HTTP API**: Simple REST API for host registration and client connections
- **Automatic Cleanup**: Removes expired host registrations after 30 minutes
- **Health Check**: Endpoint to monitor server status

## Installation

1. Ensure Go 1.21 or later is installed
2. Navigate to the router-server directory
3. Run: `go build -o router-server.exe`

## Docker Deployment

Build and run locally:

```bash
docker build -t pixelwizard-router:local .
docker run --rm -p 9000:9000 pixelwizard-router:local
```

Or use Docker Compose:

```bash
docker compose up -d --build
```

The container exposes port `9000`.

## Usage

### Start the Server

```bash
go run main.go
```

Or build and run:

```bash
go build -o router-server.exe
./router-server.exe
```

The server will start on port 9000 by default.

## API Endpoints

### POST /register

Register a host and receive a connection code.

**Request Body:**
```json
{
  "hostId": "unique-host-id",
  "hostName": "My Computer",
  "hostEndpoint": "192.168.1.100:8888"
}
```

**Response:**
```json
{
  "connectionCode": "ABC123",
  "hostId": "unique-host-id"
}
```

### POST /connect

Connect to a host using a connection code.

**Request Body:**
```json
{
  "connectionCode": "ABC123",
  "clientId": "unique-client-id"
}
```

**Response:**
```json
{
  "hostEndpoint": "192.168.1.100:8888",
  "success": true,
  "message": "Connected"
}
```

### GET /health

Check server health and status.

**Response:**
```json
{
  "status": "healthy",
  "hosts": 5,
  "uptime": "1h23m45s"
}
```

## Configuration

- **Port**: 9000 (hardcoded, can be changed in main.go)
- **Code Length**: 6 characters
- **Host Cleanup**: 30 minutes
- **Cleanup Interval**: 5 minutes

## Integration with Remote Desktop App

1. Start the router server
2. In the Remote Desktop app, select "Server Mode"
3. Choose "Router Server (Requires Router)"
4. Enter router server address (e.g., localhost:9000)
5. Click "Register & Start" to get a connection code
6. Share the connection code with the client
7. Client enters the code and router server provides the host endpoint

## Performance

Go was chosen for this router server because:
- High performance and low latency
- Excellent concurrency support (goroutines)
- Minimal memory footprint
- Fast compilation and deployment
- Cross-platform support

## Security Notes

- This is a basic implementation without authentication
- For production use, add:
  - TLS/HTTPS support
  - Authentication tokens
  - Rate limiting
  - IP whitelisting
  - Input validation
