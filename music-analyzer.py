# file: audio_to_world_prompt.py

import os
import json
from dotenv import load_dotenv
from openai import OpenAI

# 1) Load API key from .env
load_dotenv()

api_key = "YOUR_API_KEY_HERE"
if not api_key:
    raise ValueError("OPENAI_API_KEY not found in .env")

client = OpenAI(api_key=api_key)

# 2) Change this to your audio file path
AUDIO_FILE = "test2.mp3"

# 3) Transcribe the audio
with open(AUDIO_FILE, "rb") as f:
    transcript = client.audio.transcriptions.create(
        model="gpt-4o-transcribe",
        file=f,
    )

transcript_text = transcript.text

# 4) Ask GPT to describe the music/audio vibe and generate a world prompt
prompt = f"""
You are helping build a world-model demo from an audio clip.

The following text comes from transcribed or interpreted audio:
{transcript_text}

Your job is to turn this into a prompt that works well for a 3D world generation model like World Labs Marble.

Important rules:
- Focus on describing a spatial environment or place, not a story or narration.
- Convert abstract feelings or music vibes into concrete visual details.
- Mention environment, atmosphere, lighting, materials, colors, scale, and major objects.
- Make the world feel coherent and explorable.
- Avoid dialogue, plot, camera instructions, and meta commentary.
- The world prompt should be vivid but concise.
- If the transcript is sparse or unclear, infer a plausible environment from the emotional tone.

Please do the following:
1. Briefly describe what the audio feels like.
2. Infer the vibe/mood in a few words.
3. Write one vivid world prompt optimized for Marble.
4. Write one short background music description that matches the world.

Return STRICT JSON in this exact format:
{{
  "audio_summary": "short paragraph",
  "vibe": "few words",
  "world_prompt": "one vivid spatial world prompt for a 3D environment",
  "music_prompt": "one short music description"
}}
"""

response = client.responses.create(
    model="gpt-4o-mini",
    input=prompt,
)

output_text = response.output_text.strip()

print("=== Transcript ===")
print(transcript_text)
print()

print("=== Model Output ===")
print(output_text)
print()

# 5) Optional: parse JSON
try:
    result = json.loads(output_text)
    print("=== Parsed JSON ===")
    print(json.dumps(result, indent=2, ensure_ascii=False))
except json.JSONDecodeError:
    print("Could not parse JSON. Raw output shown above.")