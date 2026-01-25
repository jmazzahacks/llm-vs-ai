#!/bin/bash
# Run the VS AI Bot Agent with OpenRouter configuration

# Load .env file into shell environment
set -a
source .env
set +a

# Activate venv and run agent
source bin/activate
python agent.py
