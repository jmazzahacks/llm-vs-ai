#!/bin/bash
# Build and publish package to PyPI

# Activate virtual environment
source bin/activate

# Clean previous builds
rm -rf dist/*

# Build package
python -m build

# Upload to PyPI
python -m twine upload dist/*
