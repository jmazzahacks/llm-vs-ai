#!/usr/bin/env python
"""
Test script to verify the Claude Agent SDK can see the vsai MCP tools.
"""

import asyncio
from claude_agent_sdk import query, ClaudeAgentOptions


MCP_SERVER_PATH = "/Users/jason/Sync/code/Learn/VintageStory-AI/mcp-server"


async def main() -> None:
    print("Starting Claude Agent with vsai MCP server...")
    print("Asking agent to list available MCP tools...\n")

    async for message in query(
        prompt="List all available MCP tools from the vsai server. Just list the tool names and a one-line description for each.",
        options=ClaudeAgentOptions(
            mcp_servers={
                "vsai": {
                    "command": f"{MCP_SERVER_PATH}/bin/python",
                    "args": [f"{MCP_SERVER_PATH}/vsai_server.py"]
                }
            },
            permission_mode="bypassPermissions"
        )
    ):
        if hasattr(message, "result"):
            print("=== Agent Result ===")
            print(message.result)
        elif hasattr(message, "content"):
            print(message.content)


if __name__ == "__main__":
    asyncio.run(main())
