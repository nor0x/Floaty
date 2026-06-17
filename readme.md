# Floaty
floaty is an app that lives in your menubar / taskbar and can also be always on top of your other windows. It's an AI-powered assistant that has local memory which is automatically filled by interacting with the floating overlay - via the accessibility APIs it can read the content of your screen and also can capture screenshots.

In addition to the floating overlay, it also has a chat interface where you can ask questions and have conversations with the assistant. The chat interface also has a memory section where you can see all the information that the assistant has gathered from your interactions.

## Features
- Floating overlay that can read the content of your screen and capture screenshots
- Chat interface for asking questions and having conversations with the assistant
- Memory section where you can see all the information that the assistant has gathered
- Local memory that is automatically filled by interacting with the floating overlay
- Custom Skills (Agent Skills (Skill.MD)) and MCP (Model Context Protocol) support for tool calls

The overlay should have the image (floaty_ring.png) which is a swimming ring that has a hole in the middle where you can see the content of your screen. The overlay should be draggable (with natural rotation of the ring) and contain multiple buttons for different actions (e.g. capture screenshot, read screen content, open chat interface, etc.). The overlay should also have a settings button where you can configure the assistant's preferences and settings. Other than that the overlay should be as minimalistic and non-intrusive as possible without the regular window chrome and borders.

# Local First Approach
- memory, skills and logs are all locally stored in the users home directory under a .floaty folder where there is also a sqlite database for memory + vector search and a config json file for settings and preferences.


# Stack
- .NET MAUI for the UI and cross-platform support
- C# for the logic and functionality (Microsoft.Extensions.AI for AI integration)
- SQLite with vector search for the local memory
- OpenAI API for the AI assistant capabilities (more providers to be added in the future)
- Accessibility APIs for reading screen content and capturing screenshots
