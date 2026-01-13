# T.H.U.V.U. Docker Deployment

Multi-container Docker setup for T.H.U.V.U. (Tool for Heuristic Universal Versatile Usage).

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│                    Docker Network                            │
│                                                              │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐     │
│  │   thuvu     │    │ postgres-rag│    │   ollama    │     │
│  │  (Web UI)   │───▶│  (pgvector) │    │   (LLM)     │     │
│  │  Port 5000  │    │  Port 5432  │    │ Port 11434  │     │
│  └─────────────┘    └─────────────┘    └─────────────┘     │
│         │                                     ▲             │
│         └─────────────────────────────────────┘             │
│                    LLM Inference                            │
└─────────────────────────────────────────────────────────────┘
```

## Quick Start

### Option 1: Full Stack (with local Ollama)
```bash
cd docker

# Create .env from template
cp .env.example .env

# Start all services including Ollama
docker-compose --profile full up -d

# Pull required models (first time only)
docker-compose --profile setup up
```

### Option 2: With External LLM (LM Studio, DeepSeek, etc.)
```bash
cd docker

# Edit .env to point to your LLM
# LLM_HOST=http://host.docker.internal:1234  # For LM Studio on host
# LLM_HOST=https://api.deepseek.com          # For DeepSeek API

docker-compose up -d thuvu postgres-rag
```

### Option 3: RAG Database Only
```bash
cd docker
docker-compose up -d postgres-rag
```

## Services

| Service | Port | Description |
|---------|------|-------------|
| thuvu | 5000 | Web UI and Agent |
| postgres-rag | 5433 | PostgreSQL with pgvector |
| ollama | 11434 | Local LLM server (optional) |

## Configuration

### Environment Variables

Create a `.env` file from the template:
```bash
cp .env.example .env
```

Key variables:
| Variable | Default | Description |
|----------|---------|-------------|
| `POSTGRES_PASSWORD` | thuvu_secret | Database password |
| `LLM_HOST` | http://ollama:11434 | LLM API endpoint |
| `LLM_MODEL` | qwen2.5-coder:14b | Model to use |
| `EMBEDDING_HOST` | http://ollama:11434 | Embedding service |
| `EMBEDDING_MODEL` | nomic-embed-text | Embedding model |

### Using LM Studio on Host

To use LM Studio running on your host machine:
```env
LLM_HOST=http://host.docker.internal:1234
LLM_MODEL=your-loaded-model
```

### Using Cloud APIs

For DeepSeek or OpenAI-compatible APIs:
```env
LLM_HOST=https://api.deepseek.com
LLM_MODEL=deepseek-chat
LLM_API_KEY=your-api-key
```

## GPU Support

For NVIDIA GPU acceleration with Ollama, uncomment the GPU section in `docker-compose.yml`:

```yaml
ollama:
  deploy:
    resources:
      reservations:
        devices:
          - driver: nvidia
            count: all
            capabilities: [gpu]
```

Requires [NVIDIA Container Toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/install-guide.html).

## Volumes

| Volume | Purpose |
|--------|---------|
| `thuvu-postgres-data` | PostgreSQL data |
| `thuvu-ollama-data` | Ollama models |
| `thuvu-work-data` | Agent work directory |

## Commands

```bash
# Start all services
docker-compose --profile full up -d

# View logs
docker-compose logs -f thuvu
docker-compose logs -f ollama

# Stop services
docker-compose down

# Reset everything (including data)
docker-compose down -v

# Rebuild after code changes
docker-compose build thuvu
docker-compose up -d thuvu

# Connect to PostgreSQL
docker exec -it thuvu-postgres-rag psql -U thuvu -d thuvu_rag

# Run Ollama commands
docker exec -it thuvu-ollama ollama list
docker exec -it thuvu-ollama ollama pull codellama:13b
```

## Mounting Host Projects

To work on projects from your host machine, add a volume mount:

```yaml
thuvu:
  volumes:
    - /path/to/your/projects:/projects:rw
```

Then set the work directory in the Web UI to `/projects/your-project`.

## Health Checks

All services have health checks configured:
- **thuvu**: HTTP check on `/health`
- **postgres-rag**: `pg_isready` command
- **ollama**: Process check

View health status:
```bash
docker-compose ps
```

## Troubleshooting

### Ollama models not loading
```bash
# Check Ollama logs
docker-compose logs ollama

# Manually pull models
docker exec -it thuvu-ollama ollama pull qwen2.5-coder:14b
```

### Database connection issues
```bash
# Check PostgreSQL is healthy
docker-compose ps postgres-rag

# View PostgreSQL logs
docker-compose logs postgres-rag
```

### Web UI not accessible
```bash
# Check thuvu container status
docker-compose ps thuvu

# View application logs
docker-compose logs -f thuvu
```

## Production Considerations

1. **Security**: Change default passwords in `.env`
2. **Persistence**: Ensure volumes are backed up
3. **Resources**: Allocate sufficient memory for LLM inference
4. **Networking**: Consider using a reverse proxy (nginx/traefik)
5. **SSL**: Add TLS termination for production deployments

