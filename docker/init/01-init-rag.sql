-- Enable pgvector extension for vector similarity search
CREATE EXTENSION IF NOT EXISTS vector;

-- Table to store document embeddings for RAG
CREATE TABLE IF NOT EXISTS documents (
    id SERIAL PRIMARY KEY,
    content TEXT NOT NULL,
    metadata JSONB DEFAULT '{}',
    embedding vector(1536),  -- OpenAI ada-002 compatible dimensions
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Table to store code chunks for code-specific RAG
CREATE TABLE IF NOT EXISTS code_chunks (
    id SERIAL PRIMARY KEY,
    file_path TEXT NOT NULL,
    chunk_content TEXT NOT NULL,
    chunk_type TEXT,  -- 'function', 'class', 'module', etc.
    language TEXT,
    start_line INTEGER,
    end_line INTEGER,
    metadata JSONB DEFAULT '{}',
    embedding vector(1536),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Table to store conversation history for context
CREATE TABLE IF NOT EXISTS conversations (
    id SERIAL PRIMARY KEY,
    session_id UUID NOT NULL,
    role TEXT NOT NULL,  -- 'user', 'assistant', 'system'
    content TEXT NOT NULL,
    metadata JSONB DEFAULT '{}',
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP
);

-- Create indexes for vector similarity search using HNSW (faster queries)
CREATE INDEX IF NOT EXISTS documents_embedding_idx ON documents 
    USING hnsw (embedding vector_cosine_ops);

CREATE INDEX IF NOT EXISTS code_chunks_embedding_idx ON code_chunks 
    USING hnsw (embedding vector_cosine_ops);

-- Create indexes for common queries
CREATE INDEX IF NOT EXISTS code_chunks_file_path_idx ON code_chunks (file_path);
CREATE INDEX IF NOT EXISTS code_chunks_language_idx ON code_chunks (language);
CREATE INDEX IF NOT EXISTS conversations_session_id_idx ON conversations (session_id);

-- Function to update the updated_at timestamp
CREATE OR REPLACE FUNCTION update_updated_at_column()
RETURNS TRIGGER AS $$
BEGIN
    NEW.updated_at = CURRENT_TIMESTAMP;
    RETURN NEW;
END;
$$ language 'plpgsql';

-- Triggers to auto-update updated_at
CREATE TRIGGER update_documents_updated_at
    BEFORE UPDATE ON documents
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

CREATE TRIGGER update_code_chunks_updated_at
    BEFORE UPDATE ON code_chunks
    FOR EACH ROW
    EXECUTE FUNCTION update_updated_at_column();

-- Grant permissions to thuvu user
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO thuvu;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO thuvu;
