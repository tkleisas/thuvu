# THUVU RAG Database (PostgreSQL + pgvector)

PostgreSQL database with pgvector extension for RAG (Retrieval-Augmented Generation).

## Quick Start

```bash
cd docker
docker-compose up -d
```

## Connection Details

| Property | Value |
|----------|-------|
| Host | localhost |
| Port | 5433 |
| Database | thuvu_rag |
| User | thuvu |
| Password | thuvu_secret |

**Connection string:**
```
Host=localhost;Port=5433;Database=thuvu_rag;Username=thuvu;Password=thuvu_secret
```

> **Note:** Port 5433 is used to avoid conflicts with other PostgreSQL instances.

## Schema

### `documents` table
General document storage with embeddings for semantic search.

### `code_chunks` table
Code-specific storage with file paths, line numbers, and language metadata.

### `conversations` table
Conversation history storage for context retrieval.

## Commands

```bash
# Start the database
docker-compose up -d

# Stop the database
docker-compose down

# View logs
docker-compose logs -f postgres-rag

# Reset database (delete all data)
docker-compose down -v
docker-compose up -d

# Connect with psql
docker exec -it thuvu-postgres-rag psql -U thuvu -d thuvu_rag
```

## Vector Search Examples

```sql
-- Find similar documents (cosine similarity)
SELECT id, content, 1 - (embedding <=> '[0.1, 0.2, ...]'::vector) AS similarity
FROM documents
ORDER BY embedding <=> '[0.1, 0.2, ...]'::vector
LIMIT 5;

-- Find similar code chunks
SELECT file_path, chunk_content, chunk_type
FROM code_chunks
ORDER BY embedding <=> $1
LIMIT 10;
```
