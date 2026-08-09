This is a repo for a F# MCP stdio server that add vision capabilities to a pure text-to-text LLM.
The server should have two functions :
- analyze_image: takes one or more images or image URLs as input and returns a textual description of the image(s).
- ocr_image: takes one or more images or image URLs as input and returns the text extracted from the image(s) using Optical Character Recognition (OCR).

Use the FsMcp F# library (which wraps the official C# MCP SDK; do not use the C# SDK attributes directly).
the mcp should take a openai compatible endpoint and a model name as input parameters, and should be able to handle requests from the LLM in a standard format.
Test yourself the server with a sample image and url image and verify that the analyze_image and ocr_image functions work as expected. Make sure to handle any errors or exceptions that may occur during the processing of the images.

For testing purposes, http://macbook-m4-wifi.lan:8080/v1 as api endpoint and "ministral-mini" as model name can be used (note: ministral-mini is currently broken — use http://localhost:8080/v1 with a working model such as qwen3.6-moe:instruct instead).
