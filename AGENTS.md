# Description of Agents
Create a coding agent that can perform specific tasks autonomously. The agent should be able to understand instructions, process data, and execute actions based on predefined rules or machine learning models.
The agent will use local llms to ensure data privacy and reduce latency. It should be capable of handling various types of inputs, including text, images, and structured data. The agent
should also have the ability to learn from its interactions and improve its performance over time. The agent will connect to local databases and APIs to fetch necessary information and perform actions.
Initially the llm will be served using a Rest API in LM Studio, but future versions may include direct integration with local llm libraries for better performance.

## Features
- **Instruction Understanding**: The agent can parse and comprehend user instructions to determine the required actions.
- **Data Processing**: The agent can process various types of data, including text, images, and structured data formats.
- **Action Execution**: The agent can perform actions based on predefined rules or machine learning models.
- **Programming language support**: Emphasis on C# and .NET programming environments.
- **RAG (Retrieval-Augmented Generation)**: The agent can retrieve relevant information from local databases and APIs to enhance its responses. Use PostgreSQL as the primary database.
- **Learning Capability**: The agent can learn from its interactions and improve its performance over time.
- LLM ingegration: Initially via REST API (LM Studio), with plans for direct integration in future versions. The API is OpenAI compatible.
- configuration will be using appsettings.json for easy setup and modification.
- Logging and Monitoring: The agent will include logging and monitoring features to track its performance and identify areas for improvement.
- Tool calling: The agent will expose an interface for tool calling to extend its capabilities, based on typescript as described in the paper https://www.anthropic.com/engineering/code-execution-with-mcp

## Technologies Used
- Local LLMs (served via REST API in LM Studio)
- C# and .NET
- PostgreSQL database installed using Docker