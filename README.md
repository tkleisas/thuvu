# T.H.U.V.U. — Tool for Heuristic Universal Versatile Usage
<img src="images/thuvu.png" width="300" alt="T.H.U.V.U. Logo">

## Why this exists
I vibe-coded this agent in a few hours using mainly ChatGPT 5 in order to better understand the mechanics of AI agents and see 
how far can I go by using local LLMs. I did this because I was disappointed by the current state of the cli tools
that use local LLMs and I wanted to create a simple agent that can use tools and chat with the user. Obviously,
the inspiration for this project is Claude Code and Gemini CLI. But I want to be able to run it locally,
without the need for an API key or internet connection. I also wanted to see how far I can go with a local LLM.
qwen/qwen3-4b-2507 is a great model for this purpose, as it supports tool usage and has a decent context window.
It is also very fast and can run on a decent laptop without a GPU.

## Models and environment used
LM Studio 0.3.23 and qwen/qwen3-4b-2507 as the local LLM. Please note that I haven't tested this
with previous versions of LM Studio, so you should use at least this version.
You can use any other model that supports tool usage. This is indicated by the hammer icon on the model card in LM studio.
For LM Studio you can increase the context window to the maximum supported by the model. That is going to 
increase the memory usage a lot. For example for qwen3-4b-2507 when loaded I can see the memory usage
going up to 38 GB. Also, if you do not have huge amounts of VRAM like me, you may have to uncheck the option
Offload KV cache to GPU memory in the model settings, because it will not fit in the GPU memory.
<img src="images/lmstudio_model_settings.png" width="600" alt="LM Studio Model Settings">

## How to run
1. Download the code and extract it to a folder. You can built the code with Visual Studio 2022 or later, vs code
with the C# extension or any other IDE that supports .NET 8.0 like Rider.
2. Start LM Studio and load the model you want to use on the Developer tab. The model will be served by default
on http://localhost:1234.
3. Run the thuvu.exe file and the agent will start. You can chat with it by typing in the console or invoke
commands by typing a command starting with a slash, like /help that will show you the available commands.

## How to test
I tested it by requesting it to create a Fibonacci calculation program. It created the project and it 
could build the code.

## Performance/Usage
It is somehow usable on my laptop running the LLM mainly on the CPU, on a Thinkpad L14 with ryzen 5 pro 4650U 
with 64 gigs of RAM, on windows 11.
The agent is far from perfect, but it is a humble start for a local AI agent.

## Why the name thuvu?
The name is a reference to the late and great Greek comedian Thanassis Veggos who made a 2 part film series 
where the main character (ΘΒ) Θου Βου (Thou Vou) was an aspiring secret agent, studying at the
secret agent school and messing up all the tasks he was assigned.

## Next steps
- Try to make the agent safer by implementing a sandbox for the tools.
- Add more tools and commands.
- Try to compress the context to fit more information.
- Fine-tune the system prompt for better results. Right now it is too basic.
- Improve the UI by using a TUI console library.
- Try to support more programming languages and frameworks.
- Switch between different models (for example thinking and non thinking models and use thinking models
  for planning and non thinking models for code generation).