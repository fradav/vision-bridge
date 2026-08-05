This is a repo for a F# MCP stdio server that add vision capabilities to a pure text-to-text LLM.
The server should have two functions :
- analyze_image: takes an image or image UrL as input and returns a textual description of the image.
- ocr_image: takes an image or image URL as input and returns the text extracted from the image using Optical Character Recognition (OCR).

Use the C# MCP SDK 2.0
the mcp should take a openai compatible endpoint and a model name as input parameters, and should be able to handle requests from the LLM in a standard format.
Test yourself the server with a sample image and url image and verify that the analyze_image and ocr_image functions work as expected. Make sure to handle any errors or exceptions that may occur during the processing of the images.

For testing purposes, http://localhost:8080/v1 as api endpoint and "qwen3.6-moe:instruct" as model name can be used.
