# =========================
# LLM-DRIVEN XR SERVER
# No keywords — pure natural language -> LLM -> Unity commands
# Includes: Meshy.ai 3D generation, OpenAI image/texture generation
# =========================

from fastapi import FastAPI, File, Form, UploadFile, BackgroundTasks, HTTPException
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel
from typing import Optional, Dict, Any, List
from PIL import Image, ImageDraw
import os, json, re, time, base64, io, socket, tempfile
from datetime import datetime
from faster_whisper import WhisperModel
from openai import OpenAI
from dotenv import load_dotenv
import requests
import uuid as _uuid
import asyncio
import websockets
from fastapi import Body

load_dotenv()

# Shared model settings, stored alongside this server script.
# Model IDs remain compatible defaults until you explicitly update ai_models.json.
CONFIG_PATH = os.getenv("XR_AI_CONFIG", os.path.join(os.path.dirname(os.path.abspath(__file__)), "ai_models.json"))

def load_ai_config():
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    required = ("speech", "openai", "meshy")
    if not all(k in cfg and isinstance(cfg[k], dict) for k in required):
        raise ValueError("ai_models.json must define speech, openai and meshy objects")
    return cfg

AI_CONFIG = load_ai_config()
SPEECH_CFG = AI_CONFIG["speech"]
OPENAI_CFG = AI_CONFIG["openai"]
MESHY_CFG = AI_CONFIG["meshy"]

app = FastAPI()

SERVER_VERSION = "SCALING_V3"
print("===================================")
print("SERVER VERSION:", SERVER_VERSION)
print("===================================")

# =========================
# CONFIG
# =========================
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
client = OpenAI(api_key=OPENAI_API_KEY)
print("OPENAI_API_KEY loaded:", bool(OPENAI_API_KEY))

# "o4-mini" is good for structured JSON
LLM_MODEL = OPENAI_CFG["command_model"]

# Keep the terminal concise during experiments. Set XR_VERBOSE_LOGGING=1 to restore details.
VERBOSE_LOGGING = os.getenv("XR_VERBOSE_LOGGING", "0").strip().lower() in ("1", "true", "yes", "on")


def vprint(*args, **kwargs):
    if VERBOSE_LOGGING:
        print(*args, **kwargs)


def get_local_ip():
    """Return the LAN IP that other Unity clients can use to reach this server."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.connect(("8.8.8.8", 80))
        return sock.getsockname()[0]
    except Exception:
        return "127.0.0.1"
    finally:
        sock.close()

PUBLIC_BASE_URL = os.getenv("PUBLIC_BASE_URL", f"http://{get_local_ip()}:8000")
print("PUBLIC_BASE_URL:", PUBLIC_BASE_URL)


BASE_PATH = os.path.join(
    os.path.expanduser("~"), "AppData", "LocalLow", "DefaultCompany", "VRTApp-TestLocal"
)
os.makedirs(BASE_PATH, exist_ok=True)

TRANSCRIPT_FILE = os.path.join(BASE_PATH, "unity_transcripts.txt")
TEMP_AUDIO_PATH = os.path.join(BASE_PATH, "temp.wav")

MODEL_DIR = os.path.join(BASE_PATH, "GeneratedModels")
os.makedirs(MODEL_DIR, exist_ok=True)

POSTER_DIR = os.path.join(BASE_PATH, "posters")
os.makedirs(POSTER_DIR, exist_ok=True)

TEXTURE_DIR = os.path.join(BASE_PATH, "textures")
os.makedirs(TEXTURE_DIR, exist_ok=True)

# Serve generated files to Unity over HTTP
app.mount("/files", StaticFiles(directory=MODEL_DIR), name="files")
app.mount("/posters", StaticFiles(directory=POSTER_DIR), name="posters")
app.mount("/textures", StaticFiles(directory=TEXTURE_DIR), name="textures")

# =========================
# MESHY CONFIG
# =========================
MESHY_API_KEY = os.getenv("MESHY_API_KEY")
print("MESHY_API_KEY loaded:", bool(MESHY_API_KEY))

MESHY_BASE = "https://api.meshy.ai"
MESHY_TEXT2_3D_CREATE = f"{MESHY_BASE}/openapi/{MESHY_CFG['api_version']}/text-to-3d"
MESHY_TEXT2_3D_GET = f"{MESHY_BASE}/openapi/{MESHY_CFG['api_version']}/text-to-3d"

# In-memory job tracker for background Meshy tasks
jobs: Dict[str, Dict[str, Any]] = {}

# In-memory store for commands awaiting user confirmation
pending_commands: Dict[str, Dict[str, Any]] = {}

# =========================
# WHISPER
# =========================
print("Loading Whisper model...")
whisper_model = None
if SPEECH_CFG.get("engine") == "faster-whisper":
    whisper_model = WhisperModel(SPEECH_CFG["model"], device=SPEECH_CFG["device"], compute_type=SPEECH_CFG["compute_type"])

def transcribe_audio_bytes(audio_bytes: bytes) -> str:
    """Transcribe finalized WAV bytes; realtime streaming requires a separate socket pipeline."""
    global whisper_model
    engine = SPEECH_CFG.get("engine", "faster-whisper")
    if engine == "openai_batch":
        f = io.BytesIO(audio_bytes)
        f.name = "voice.wav"
        try:
            result = client.audio.transcriptions.create(model=SPEECH_CFG["model"], file=f)
            return result.text.strip()
        except Exception as ex:
            print("[Transcription API failed; fallback to local Whisper]", ex, flush=True)
            engine = SPEECH_CFG.get("fallback_engine", "faster-whisper")
    if engine != "faster-whisper":
        raise ValueError("Unsupported speech.engine. Use openai_batch or faster-whisper. Streaming requires a WebSocket client/server workflow.")
    if whisper_model is None:
        whisper_model = WhisperModel(SPEECH_CFG.get("fallback_model", "small"), device=SPEECH_CFG["device"], compute_type=SPEECH_CFG["compute_type"])
    # Unique audio file so two concurrent users cannot overwrite each other's recordings.
    with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
        tmp.write(audio_bytes)
        wav_path = tmp.name
    try:
        segments, _ = whisper_model.transcribe(wav_path, beam_size=SPEECH_CFG.get("beam_size", 3), task=SPEECH_CFG.get("task", "transcribe"))
        return "".join(segment.text for segment in segments).strip()
    finally:
        try: os.unlink(wav_path)
        except OSError: pass
print("Whisper model ready.")

# =========================
# SYSTEM PROMPT
# =========================
SYSTEM_PROMPT = """
You are an AI agent that controls a Unity XR/VR environment by interpreting natural speech commands.

Your ONLY job is to output a valid JSON object with a "commands" array. No explanations. No markdown. No extra text. Just raw JSON.

CRITICAL: If you output ANYTHING other than a raw JSON object, the entire pipeline fails. Do not explain your reasoning. Do not say "I cannot". Do not add notes. Output ONLY the JSON object, starting with { and ending with }.

=== AVAILABLE ACTIONS ===

1. generate_model
   - Use ONLY when the user wants to generate a new 3D object/model from text.
   - Required fields: "prompt"
   - Optional fields: "name", "stage", "art_style"

2. create_poster
   - Use ONLY when the user wants to generate a poster/image and place or use it as a poster.
   - Required fields: "image_prompt"
   - Optional fields: "width_m", "height_m"

3. set_wall_texture
   - Use ONLY when the user wants to generate/apply a texture.
   - Required fields: "texture_prompt"
   - Optional fields: "target"

4. run_code
   - This is the PRIMARY and DEFAULT action for EVERY actionable XR request that is NOT poster generation, texture generation, or 3D model generation.
   - It generates and attaches a C# script at runtime.
   - Use it for ALL of the following (and anything else not covered by the three actions above):
     movement, rotation, color changes, duplication-like behaviours, particles,
     animation, interaction, effects, procedural logic, following, constraints, stacking,
     placement, lighting, deletion, spawning primitives, physics, UI, audio, and any
     other Unity behaviour whatsoever.
   - If in doubt, use run_code.
   - Required fields:
     - "targets": array of zero or more exact GameObject names
     - "reference_objects": array of zero or more exact GameObject names
     - "relation": optional relation string or null
     - "behaviour_prompt": concise but implementation-oriented instruction for generated Unity C# code

5. scale
   - Use ONLY for proportional resize / grow / shrink:
     "make this bigger", "double the size", "make it half as big", "make the poster larger".
   - NEVER use run_code for resizing.
   - Required fields:
     - "factor": multiplicative factor. >1 grows, <1 shrinks.
   - Map language to factor: slightly bigger -> 1.2, bigger -> 1.5, much bigger/double -> 2.0,
     slightly smaller -> 0.8, smaller -> 0.6, half -> 0.5.
   - Optional "targets": exact selected object name. If omitted Unity uses the locked selection.

6. set_dimensions
   - Use for ABSOLUTE requested dimensions in meters.
   - NEVER use run_code for this.
   - Use when the user says things like:
     "make this poster 5 by 5 meters",
     "set this poster to 3 x 2 meters",
     "increase the height of this poster to 3 meters",
     "set the width to 4 meters".
   - Required:
     - "targets": array containing the exact object name when available.
   - Optional numeric fields (omit dimensions the user did not specify):
     - "width_m"
     - "height_m"
     - "depth_m"
   - For posters, width_m and height_m are the poster's real world dimensions.
   - A poster is independent from its parent wall: resize ONLY the poster object, NEVER its parent wall.
   - Examples:
     "make this poster 5x5" -> {"action":"set_dimensions","targets":["Poster_123"],"width_m":5.0,"height_m":5.0}
     "increase the height of this poster to 3 meters" -> {"action":"set_dimensions","targets":["Poster_123"],"height_m":3.0}

7. no_action
   - Use when the input is purely conversational, unclear, or no XR action should happen.

8. animate_model
   - Use ONLY for skeletal (rigged) character animation of a HUMANOID, two-legged character model:
     walk, run, jog, dance, wave, jump, idle, fight, punch, kick, sit and similar body motions.
   - Required fields:
     - "targets": array with the exact name of the character model to animate.
     - "animation": short motion name, e.g. "walk", "run", "dance", "wave", "idle", "punch".
       Use "" to only add a skeleton (rig) without playing a motion.
   - Rigging only works on humanoid characters. For non-humanoid objects (cars, trees, furniture,
     animals, buildings) use run_code for procedural motion (spin, bob, sway, move) instead.

=== OUTPUT FORMAT ===
{
  "commands": [
    { "action": "...", ... },
    { "action": "...", ... }
  ]
}

=== SIGNAL PRIORITY ===
You receive up to three signals to identify which object(s) the user means:

1. TRANSCRIPT   — what the user said. Highest weight. The user's words are the clearest expression of intent.
2. VISION_CONTEXT — a vision model's description of every object visible in the scene and their spatial relationships. High weight.
3. GAZE_TARGET  — the object the user's eye tracking landed on at the moment they started speaking. Supporting hint only — gaze can accidentally land on floors, walls, ceilings, or background objects.

Resolution rules (apply in order):
- If TRANSCRIPT + VISION_CONTEXT agree on a specific object, use that object — even if GAZE_TARGET points elsewhere.
- If the transcript uses "this", "it", "that" and GAZE_TARGET is a meaningful named object (not floor/wall/ceiling) and VISION_CONTEXT confirms that object exists, trust GAZE_TARGET.
- If GAZE_TARGET is a generic surface (floor, wall, ceiling, ground, terrain) and TRANSCRIPT or VISION_CONTEXT clearly implies a specific object, ignore GAZE_TARGET and use the object from TRANSCRIPT/VISION_CONTEXT.
- If GAZE_TARGET refers to the user's own body (e.g. AvatarBody, Player, XRRig, Camera, Hand, Controller, or any self-referential object), ignore it completely and resolve the target from TRANSCRIPT and VISION_CONTEXT instead.
- Use VISION_CONTEXT to resolve spatial references like "near the house", "next to the table", "between those two" — it describes the full scene layout.
- NEVER invent object names not grounded in at least one of TRANSCRIPT, GAZE_TARGET, or VISION_CONTEXT.
- NEVER use tags. Only use exact GameObject names.


=== OBJECT EXISTENCE / GENERATION PLANNING RULE ===
- If the user asks to place, put, move, or position an object that does NOT already exist in SCENE_OBJECTS, first create it.
- For natural objects or complex 3D assets such as tree, car, chair, animal, statue, lamp, plant, house, fountain, etc., use generate_model first.
- Then add a second run_code command that places the generated object using the requested relation.
- Use the generated model name as the target of the second command. Choose a stable generated name, e.g. Generated_Tree, Generated_Chair, Generated_Car.
- Example: "place a tree between these two cubes" means:
  1. generate_model with prompt "tree" and name "Generated_Tree"
  2. run_code with targets ["Generated_Tree"], reference_objects as the two cube GameObjects, relation "between".
- For simple Unity primitives such as cube, sphere, cylinder, capsule, plane, use run_code to spawn the primitive directly instead of generate_model.

=== STRUCTURED VISION GROUNDING RULE ===
- VISION_CONTEXT may be structured JSON. Prefer exact names from:
  - primary_references
  - visible_objects[].name
  - relations[].objects
- For phrases like "these two cubes", use the two cube-like objects listed in primary_references if present.
- For "between these two objects", put both objects in reference_objects and set relation to "between".
- If VISION_CONTEXT identifies two visible cubes as Cube and Cube.001, do not collapse them into one object. Use both.

=== CORE RULES ===
- Use animate_model ONLY for skeletal animation of humanoid characters (walk, run, dance, wave...).
- Use generate_model ONLY for generating new 3D objects/models from text.
- Use create_poster ONLY for poster/image generation.
- Use set_wall_texture ONLY for texture generation/application.
- For proportional resize / grow / shrink requests, use scale (NOT run_code).
- For absolute width / height / depth / meter dimension requests, use set_dimensions (NOT run_code).
- NEVER use run_code for any resize, scale, size, width, height, depth, or dimension change.
- For EVERY other actionable XR instruction, use run_code.
- NEVER return translate, set_color, rotate, delete_object, duplicate_object, spawn_primitive, set_lighting, or any action name not listed above.
- Output ONLY raw JSON.

=== RESIZE / DIMENSION RULES ===
- Resizing existing objects MUST be deterministic.
- NEVER put resize logic into behaviour_prompt.
- NEVER generate C# that edits localScale for a resize request.
- If the request gives a factor ("twice", "half", "bigger"), use scale.
- If the request gives absolute dimensions ("3 meters high", "5x5", "3 by 2 meters"), use set_dimensions.
- If only one absolute dimension is specified, send only that field and preserve the other dimensions.
- If GAZE_TARGET is a Poster_* object, that exact Poster_* object is the resize target.
- NEVER substitute the poster's parent wall as the target.

Examples:
User: "make this poster 3 by 3 meters"
GAZE_TARGET: "Poster_192040"
{"commands":[{"action":"set_dimensions","targets":["Poster_192040"],"width_m":3.0,"height_m":3.0}]}

User: "increase the height of this poster to 3 meters"
GAZE_TARGET: "Poster_192040"
{"commands":[{"action":"set_dimensions","targets":["Poster_192040"],"height_m":3.0}]}

User: "make this chair twice as big"
GAZE_TARGET: "Generated_Chair"
{"commands":[{"action":"scale","targets":["Generated_Chair"],"factor":2.0}]}

=== run_code SCHEMA ===
Each run_code command must use:
- action: "run_code"
- targets: array of zero or more exact GameObject names
- reference_objects: array of zero or more exact GameObject names
- relation: optional relation string or null
- behaviour_prompt: concise implementation instruction for generated Unity C# code

=== RULES FOR TARGETS VS REFERENCE OBJECTS ===
- The object that the script should be attached to or directly modify belongs in "targets".
- Supporting/context objects belong in "reference_objects".
- If the user says "generate fire", "add smoke", "make this burn", "put water here", "add sparks to this", and GAZE_TARGET exists, put GAZE_TARGET in "targets".
- Do NOT put the main acted-on object only in "reference_objects".
- For relational commands like "put this on the table", put the moved object in "targets" and the supporting object in "reference_objects".

=== HOW TO WRITE behaviour_prompt ===
- For "put on ground", "place on floor", "drop to ground", or similar commands:
  - preserve the target object's current x and z position unless the user explicitly asks to move it somewhere else
  - only adjust y so the bottom of the object rests on the top surface of the ground/floor
  - do not move the object to the center of the ground unless explicitly requested
  - if a known floor/ground object exists, use it as a reference object

The behaviour_prompt should read like a precise implementation brief for a Unity C# script.
Include:
- what object(s) are affected
- any reference object(s) being used for context
- whether the action happens once, continuously, on Start, on Update, on trigger, or on click
- exact motion / color / scale / timing values
- coordinate intent when relevant
- any smoothing, interpolation, looping, spawn position, or cleanup rules

=== PARTICLE / VFX RULES ===
For fire, smoke, water, fountain, sparks, mist, explosion, waterfall, or similar effects:
- Prefer creating and configuring a ParticleSystem directly in code.
- Do NOT rely on inspector assignment.
- Do NOT assume a prefab field is assigned.
- The generated runtime code should work immediately after being attached.
- Use renderer bounds of the target object to choose a sensible spawn point.
- Explicitly say to create a child GameObject, add a ParticleSystem, configure it, and call Play().
- If the user is looking at an object and asks for the effect on that object, put that object in "targets".

=== EXAMPLES ===

User: "put this on the ground"
GAZE_TARGET: "Cube_01"
{"commands":[{"action":"run_code","targets":["Cube_01"],"reference_objects":["ground_floor_window_frame"],"relation":"on_ground","behaviour_prompt":
"on Start, place Cube_01 on top of ground_floor_window_frame by preserving Cube_01 current x and z position and only adjusting y so the bottom of Cube_01 
rests on the ground surface without intersecting it; do not move Cube_01 to the center of the ground"}]}

User: "move it 2 meters to the right"
GAZE_TARGET: "Cube_03"
{"commands":[{"action":"run_code","targets":["Cube_03"],"reference_objects":[],"relation":null,"behaviour_prompt":"move Cube_03 2 meters to the right relative to its current position when the script starts, then stop"}]}

User: "turn it blue"
GAZE_TARGET: "Sphere_01"
{"commands":[{"action":"run_code","targets":["Sphere_01"],"reference_objects":[],"relation":null,"behaviour_prompt":"change the visible renderers of Sphere_01 and its children to blue when the script starts"}]}

User: "put this object on the top of the table"
GAZE_TARGET: "Cube_02"
{"commands":[{"action":"run_code","targets":["Cube_02"],"reference_objects":["Tables"],"relation":"on_top","behaviour_prompt":"place Cube_02 centered on top of Tables using renderer bounds when the script starts so Cube_02 rests on the top surface without intersecting it"}]}

User: "generate fire"
GAZE_TARGET: "Wall_1"
{"commands":[{"action":"run_code","targets":["Wall_1"],"reference_objects":[],"relation":null,"behaviour_prompt":"on Start, create a child GameObject on Wall_1, add and configure a looping fire ParticleSystem directly in code, place it near the bottom center of Wall_1 using combined renderer bounds, and call Play immediately; do not require any prefab or inspector assignment"}]}

User: "add smoke to this"
GAZE_TARGET: "Barrel_01"
{"commands":[{"action":"run_code","targets":["Barrel_01"],"reference_objects":[],"relation":null,"behaviour_prompt":"on Start, create a child GameObject on Barrel_01, add and configure a looping smoke ParticleSystem directly in code, place it near the top center of Barrel_01 using renderer bounds, and call Play immediately; do not require any prefab or inspector assignment"}]}

User: "make this poster 3 by 3 meters"
GAZE_TARGET: "Poster_192040"
{"commands":[{"action":"set_dimensions","targets":["Poster_192040"],"width_m":3.0,"height_m":3.0}]}

User: "set the height of this poster to 3 meters"
GAZE_TARGET: "Poster_192040"
{"commands":[{"action":"set_dimensions","targets":["Poster_192040"],"height_m":3.0}]}

User: "make a poster of a snowy mountain landscape"
{"commands":[{"action":"create_poster","image_prompt":"snowy mountain landscape","width_m":1.5,"height_m":1.0}]}

User: "apply brick texture to the wall"
{"commands":[{"action":"set_wall_texture","texture_prompt":"old red brick wall","target":"Wall"}]}

User: "create a realistic wooden chair"
{"commands":[{"action":"generate_model","prompt":"realistic wooden chair","name":"Wooden_Chair","stage":"refine"}]}

User: "move this tree near to the building"
GAZE_TARGET: "AvatarBody"
VISION_CONTEXT: "A red tree is in the background on the right. A white building is on the left in the foreground. The user likely wants to move the red tree closer to the white building."
{"commands":[{"action":"run_code","targets":["red tree"],"reference_objects":["white building"],"relation":"near","behaviour_prompt":"on Start, move the red tree so it is positioned 2 meters from the nearest edge of the white building using renderer bounds; keep the tree's Y position grounded"}]}

User: "place this tree near to the home near to this building"
GAZE_TARGET: "AvatarBody"
VISION_CONTEXT: "The scene has a red tree on the right background and a white building on the left foreground. The user wants to relocate the red tree near the white building."
{"commands":[{"action":"run_code","targets":["red tree"],"reference_objects":["white building"],"relation":"near","behaviour_prompt":"on Start, position the red tree 2 meters away from the white building's nearest face using renderer bounds; do not change the tree's Y scale or rotation"}]}


User: "place a tree between these two cubes"
VISION_CONTEXT: {"primary_references":["Cube","Cube.001"],"relations":[{"relation":"between_space","objects":["Cube","Cube.001"]}]}
{"commands":[{"action":"generate_model","prompt":"tree","name":"Generated_Tree","stage":"refine"},{"action":"run_code","targets":["Generated_Tree"],"reference_objects":["Cube","Cube.001"],"relation":"between","behaviour_prompt":"on Start, place Generated_Tree at the midpoint between Cube and Cube.001 using combined renderer bounds, then ground it on the nearest floor surface while preserving its scale and rotation"}]}

User: "place a cube between these two cubes"
VISION_CONTEXT: {"primary_references":["Cube","Cube.001"]}
{"commands":[{"action":"run_code","targets":[],"reference_objects":["Cube","Cube.001"],"relation":"between","behaviour_prompt":"on Start, create a new Unity cube primitive named Generated_Cube at the midpoint between Cube and Cube.001 using renderer bounds, set scale to Vector3.one, and ground it on the nearest floor surface"}]}

User: "make this character dance"
GAZE_TARGET: "Generated_Woman"
{"commands":[{"action":"animate_model","targets":["Generated_Woman"],"animation":"dance"}]}

User: "create a soldier that is walking"
{"commands":[{"action":"generate_model","prompt":"soldier, full body, standing","name":"Generated_Soldier","stage":"refine"},{"action":"animate_model","targets":["Generated_Soldier"],"animation":"walk"}]}

User: "make the car spin"
GAZE_TARGET: "Generated_Car"
{"commands":[{"action":"run_code","targets":["Generated_Car"],"reference_objects":[],"relation":null,"behaviour_prompt":"in Update, rotate Generated_Car continuously around its Y axis at 45 degrees per second"}]}

User: "hello how are you"
{"commands":[{"action":"no_action","reason":"conversational input, no XR action needed"}]}
"""

# =========================
# MESHY HELPERS
# =========================

def _safe_name(name: str) -> str:
    safe = re.sub(r"[^a-zA-Z0-9_\-]", "_", (name or "").strip())
    return safe if safe else "generated_model"


def _meshy_headers() -> Dict[str, str]:
    return {
        "Authorization": f"Bearer {MESHY_API_KEY}",
        "Accept": "application/json",
        "Content-Type": "application/json",
    }


def _meshy_create_task(mode: str, payload: Dict[str, Any]) -> str:
    body = {"mode": mode, **payload}
    resp = requests.post(MESHY_TEXT2_3D_CREATE, headers=_meshy_headers(), json=body, timeout=60)
    if not resp.ok:
        print("[meshy] CREATE FAILED:", resp.status_code, resp.text, flush=True)
        resp.raise_for_status()
    data = resp.json()
    task_id = data.get("result")
    if not task_id:
        raise RuntimeError(f"Meshy create task response missing 'result': {data}")
    return task_id


def _meshy_get_task(task_id: str) -> Dict[str, Any]:
    url = f"{MESHY_TEXT2_3D_GET}/{task_id}"
    resp = requests.get(url, headers=_meshy_headers(), timeout=60)
    if not resp.ok:
        print("[meshy] GET FAILED:", resp.status_code, resp.text, flush=True)
        resp.raise_for_status()
    return resp.json()


def _download_to_file(url: str, out_path: str):
    r = requests.get(url, timeout=180)
    if not r.ok:
        print("[meshy] DOWNLOAD FAILED:", r.status_code, r.text[:200], flush=True)
        r.raise_for_status()
    with open(out_path, "wb") as f:
        f.write(r.content)


def _set_job_progress(safe: str, stage: str, task_id: str, meshy_status: str, progress: Any, phase: str = "preview"):
    try:
        p = int(progress) if progress is not None else 0
    except Exception:
        p = 0

    old_job = jobs.get(safe, {})
    jobs[safe] = {
        "stage": stage,
        "task_id": task_id,
        "meshy_status": str(meshy_status or "PENDING"),
        "progress": p,
        "status": "RUNNING",
        "phase": phase,
        "error": old_job.get("error", ""),
        # Keep last printed values so the terminal only shows NEW progress updates.
        "_last_logged_status": old_job.get("_last_logged_status"),
        "_last_logged_progress": old_job.get("_last_logged_progress"),
    }


def _log_meshy_progress_once(safe: str, phase: str, task_id: str, meshy_status: str, progress: Any):
    """
    Print Meshy progress only when status or progress changes.
    This avoids repeated terminal spam such as:
    status=IN_PROGRESS progress=99
    status=IN_PROGRESS progress=99
    status=IN_PROGRESS progress=99
    """
    try:
        p = int(progress) if progress is not None else 0
    except Exception:
        p = 0

    status = str(meshy_status or "PENDING")
    job = jobs.get(safe, {})

    if job.get("_last_logged_status") == status and job.get("_last_logged_progress") == p:
        return

    job["_last_logged_status"] = status
    job["_last_logged_progress"] = p
    jobs[safe] = job

    vprint(f"[meshy] {phase} {task_id} status={status} progress={p}", flush=True)


def _meta_path(safe: str) -> str:
    return os.path.join(MODEL_DIR, f"{safe}.meshy.json")


def _load_meta(safe: str) -> Dict[str, Any]:
    try:
        with open(_meta_path(safe), "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def _save_meta(safe: str, **fields):
    meta = _load_meta(safe)
    meta.update(fields)
    with open(_meta_path(safe), "w", encoding="utf-8") as f:
        json.dump(meta, f, indent=2)


_CHARACTER_WORDS = re.compile(
    r"\b(human|person|people|man|woman|boy|girl|child|kid|character|avatar|soldier|knight|"
    r"warrior|wizard|robot|humanoid|zombie|player|worker|farmer|merchant|sailor|guard|lady|gentleman)\b",
    re.IGNORECASE,
)


def _looks_like_character(prompt: str) -> bool:
    return bool(_CHARACTER_WORDS.search(prompt or ""))


def _generate_with_meshy_background(prompt: str, name: str, stage: str, art_style: str):
    safe = _safe_name(name)
    out_glb = os.path.join(MODEL_DIR, f"{safe}.glb")
    stage = (stage or MESHY_CFG["default_stage"]).lower()
    # Preview = untextured mesh only; refine adds the textures. Force refine when always_texture is on,
    # whatever stage the client or the LLM asked for.
    if MESHY_CFG.get("always_texture", True):
        stage = "refine"

    try:
        if not MESHY_API_KEY:
            raise RuntimeError("MESHY_API_KEY not set.")

        jobs[safe] = {
            "status": "RUNNING", "stage": stage, "task_id": None,
            "meshy_status": "PENDING", "progress": 0, "error": "",
        }

        preview_payload = {"prompt": prompt, "should_remesh": MESHY_CFG["should_remesh"], "ai_model": MESHY_CFG.get("ai_model", "latest"), "model_type": MESHY_CFG.get("model_type", "standard"), "target_formats": ["glb"]}
        # Characters generated in an A-pose rig much more reliably.
        if MESHY_CFG.get("character_pose_mode") and _looks_like_character(prompt):
            preview_payload["pose_mode"] = MESHY_CFG["character_pose_mode"]
        preview_task_id = _meshy_create_task(mode="preview", payload=preview_payload)
        # Remember the Meshy task ids so this model can be rigged/animated later.
        _save_meta(safe, prompt=prompt, preview_task_id=preview_task_id, refine_task_id="",
                   rig_task_id="", rig_expires_at=0)
        _set_job_progress(safe, stage, preview_task_id, "PENDING", 0)

        preview_task = None
        deadline = time.time() + 20 * 60
        while time.time() < deadline:
            t = _meshy_get_task(preview_task_id)
            meshy_status = t.get("status") or "PENDING"
            progress = t.get("progress") or 0
            _set_job_progress(safe, stage, preview_task_id, meshy_status, progress)
            _log_meshy_progress_once(safe, "preview", preview_task_id, meshy_status, progress)
            if meshy_status == "SUCCEEDED" and t.get("model_urls", {}).get("glb"):
                preview_task = t
                break
            if meshy_status in ("FAILED", "CANCELED"):
                raise RuntimeError(f"Meshy preview failed: {t.get('task_error') or t}")
            time.sleep(MESHY_CFG["poll_seconds"])

        if preview_task is None:
            raise RuntimeError("Timed out waiting for Meshy preview task.")

        if (stage or "preview").lower() == "preview":
            glb_url = preview_task["model_urls"]["glb"]
            _download_to_file(glb_url, out_glb)
            jobs[safe]["status"] = "DONE"
            jobs[safe]["meshy_status"] = "SUCCEEDED"
            jobs[safe]["progress"] = 100
            print(f"[meshy] preview done -> {out_glb}", flush=True)
            return

        refine_task_id = _meshy_create_task(
            mode="refine",
            payload={
                "preview_task_id": preview_task_id,
                "enable_pbr": MESHY_CFG["enable_pbr"],
                "texture_prompt": (prompt or "")[:800],   # guides texturing with the same description
                "target_formats": ["glb"],
            },
        )
        _set_job_progress(safe, stage, refine_task_id, "PENDING", 0, phase="refine")
        _save_meta(safe, refine_task_id=refine_task_id)

        refine_task = None
        deadline = time.time() + 30 * 60
        while time.time() < deadline:
            t = _meshy_get_task(refine_task_id)
            meshy_status = t.get("status") or "PENDING"
            progress = t.get("progress") or 0
            _set_job_progress(safe, stage, refine_task_id, meshy_status, progress, phase="refine")
            _log_meshy_progress_once(safe, "refine", refine_task_id, meshy_status, progress)
            if meshy_status == "SUCCEEDED" and t.get("model_urls", {}).get("glb"):
                refine_task = t
                break
            if meshy_status in ("FAILED", "CANCELED"):
                raise RuntimeError(f"Meshy refine failed: {t.get('task_error') or t}")
            time.sleep(MESHY_CFG["poll_seconds"])

        if refine_task is None:
            raise RuntimeError("Timed out waiting for Meshy refine task.")

        glb_url = refine_task["model_urls"]["glb"]
        _download_to_file(glb_url, out_glb)
        jobs[safe]["status"] = "DONE"
        jobs[safe]["meshy_status"] = "SUCCEEDED"
        jobs[safe]["progress"] = 100
        print(f"[meshy] refine done -> {out_glb}", flush=True)

    except Exception as e:
        print("[meshy] ERROR:", e, flush=True)
        jobs[safe] = {
            "status": "ERROR", "stage": stage,
            "task_id": jobs.get(safe, {}).get("task_id"),
            "meshy_status": "FAILED", "progress": 0, "error": str(e),
        }

# =========================
# IMAGE GENERATION HELPERS
# =========================

def _make_ai_poster(prompt: str, out_path: str, w: int, h: int):
    result = client.images.generate(
        model=OPENAI_CFG["poster_model"],
        prompt=f"Poster artwork, high quality, no text unless requested. {prompt}",
        size="1024x1024",
    )
    img_bytes = base64.b64decode(result.data[0].b64_json)
    img = Image.open(io.BytesIO(img_bytes)).convert("RGB")
    if (w, h) != (1024, 1024):
        img = img.resize((w, h), Image.LANCZOS)
    img.save(out_path, "PNG")


def _make_ai_texture(prompt: str, out_path: str, size_px: int = 1024):
    result = client.images.generate(
        model=OPENAI_CFG["texture_model"],
        prompt=f"Seamless tileable texture. Realistic. No perspective. Even lighting. No text. {prompt}",
        size=f"{size_px}x{size_px}",
    )
    img_bytes = base64.b64decode(result.data[0].b64_json)
    img = Image.open(io.BytesIO(img_bytes)).convert("RGB")
    img.save(out_path, "PNG")


def _make_placeholder_poster(prompt: str, out_path: str, w: int, h: int):
    img = Image.new("RGB", (w, h), (245, 245, 245))
    draw = ImageDraw.Draw(img)
    draw.rectangle([8, 8, w - 8, h - 8], outline=(40, 40, 40), width=4)
    text = (prompt or "Poster").strip()[:220]
    max_chars = 38 if w >= h else 30
    lines = [text[i:i + max_chars] for i in range(0, len(text), max_chars)]
    y = 40
    for ln in lines[:12]:
        draw.text((30, y), ln, fill=(20, 20, 20))
        y += 28
    img.save(out_path, "PNG")

# =========================
# TEXT-TO-3D ENDPOINT
# =========================

class TextTo3DRequest(BaseModel):
    prompt: str
    name: str
    stage: Optional[str] = "refine"
    art_style: Optional[str] = "realistic"


@app.post("/api/text-to-3d")
def text_to_3d(req: TextTo3DRequest, background_tasks: BackgroundTasks):
    safe = _safe_name(req.name)
    glb_filename = f"{safe}.glb"
    glb_path = os.path.join(MODEL_DIR, glb_filename)

    if not MESHY_API_KEY:
        return JSONResponse(
            status_code=500,
            content={"status": "FAILED", "progress": 0, "message": "MESHY_API_KEY missing."},
        )

    if os.path.exists(glb_path):
        return JSONResponse(status_code=200, content={
            "status": "SUCCEEDED", "progress": 100,
            "downloadUrl": f"{PUBLIC_BASE_URL}/files/{glb_filename}",
        })

    job = jobs.get(safe)

    if job and job.get("status") == "RUNNING":
        # Report one overall progress (preview 0-50 %, refine 50-100 %) and never "SUCCEEDED"
        # while still running: otherwise the client thinks the job is done when the
        # untextured preview finishes, before the textured refine has even started.
        p = int(job.get("progress", 0) or 0)
        if job.get("stage") == "refine":
            p = 50 + p // 2 if job.get("phase") == "refine" else p // 2
        return JSONResponse(status_code=202, content={
            "status": "IN_PROGRESS",
            "progress": min(p, 99),
        })

    if job and job.get("status") == "ERROR":
        return JSONResponse(status_code=500, content={
            "status": "FAILED", "progress": 0,
            "message": job.get("error", "Unknown error"),
        })

    stage = (req.stage or "preview").lower()
    art_style = (req.art_style or "realistic").lower()
    vprint(f"[text-to-3d] starting name={req.name} safe={safe} stage={stage} art_style={art_style}", flush=True)

    jobs[safe] = {
        "status": "RUNNING", "stage": stage, "task_id": None,
        "meshy_status": "PENDING", "progress": 0, "error": "",
    }

    background_tasks.add_task(_generate_with_meshy_background, req.prompt, req.name, stage, art_style)
    return JSONResponse(status_code=202, content={"status": "PENDING", "progress": 0})

# =========================
# RIGGING + ANIMATION (Meshy)
# =========================
# Flow: existing model -> Meshy Rigging (humanoids only) -> Meshy Animation (one library clip)
#       -> animated GLB saved as GeneratedModels/<model>__<clip>.glb and served to Unity.

MESHY_RIG_URL = f"{MESHY_BASE}/openapi/v1/rigging"
MESHY_ANIM_URL = f"{MESHY_BASE}/openapi/v1/animations"
MESHY_ANIM_LIBRARY_URL = f"{MESHY_BASE}/openapi/v1/animations/library"

# Rig/animation jobs keyed by output name (<model>__<clip>)
anim_jobs: Dict[str, Dict[str, Any]] = {}
_anim_library_cache: Optional[list] = None

# When no clip name matches the requested word, fall back to the first clip of this category.
ANIMATION_CATEGORY_HINTS = {
    "walk": "WalkAndRun", "walking": "WalkAndRun", "run": "WalkAndRun", "running": "WalkAndRun",
    "jog": "WalkAndRun", "sprint": "WalkAndRun",
    "dance": "Dancing", "dancing": "Dancing",
    "fight": "Fighting", "punch": "Fighting", "kick": "Fighting", "attack": "Fighting", "box": "Fighting",
}


def _meshy_create(url: str, body: Dict[str, Any]) -> str:
    resp = requests.post(url, headers=_meshy_headers(), json=body, timeout=120)
    if not resp.ok:
        raise RuntimeError(f"Meshy {resp.status_code}: {resp.text[:300]}")
    task_id = resp.json().get("result")
    if not task_id:
        raise RuntimeError(f"Meshy create response missing 'result': {resp.text[:300]}")
    return task_id


def _meshy_wait(url: str, task_id: str, job_key: str, p_from: int, p_to: int, timeout_s: int = 20 * 60) -> Dict[str, Any]:
    """Poll a Meshy v1 task until it finishes, mapping its 0-100 progress onto p_from..p_to."""
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        resp = requests.get(f"{url}/{task_id}", headers=_meshy_headers(), timeout=60)
        if not resp.ok:
            raise RuntimeError(f"Meshy {resp.status_code}: {resp.text[:300]}")
        t = resp.json()
        status = t.get("status") or "PENDING"
        p = int(t.get("progress") or 0)
        if job_key in anim_jobs:
            anim_jobs[job_key]["progress"] = int(p_from + (p_to - p_from) * p / 100)
        if status == "SUCCEEDED":
            return t
        if status in ("FAILED", "CANCELED"):
            raise RuntimeError((t.get("task_error") or {}).get("message") or f"Meshy task {status}")
        time.sleep(MESHY_CFG["poll_seconds"])
    raise RuntimeError("Timed out waiting for Meshy task")


def _animation_library() -> list:
    """Meshy's animation library (free to list). Cached for the lifetime of the server."""
    global _anim_library_cache
    if _anim_library_cache is None:
        resp = requests.get(MESHY_ANIM_LIBRARY_URL, headers=_meshy_headers(), timeout=60)
        if not resp.ok:
            raise RuntimeError(f"Meshy {resp.status_code}: {resp.text[:300]}")
        data = resp.json()
        if isinstance(data, dict):
            data = data.get("result") or data.get("data") or data.get("items") or []
        _anim_library_cache = data if isinstance(data, list) else []
    return _anim_library_cache


def _resolve_animation(animation: str):
    """Map 'dance' / 'walk' / '92' to (action_id, clip name). Returns (None, None) if nothing matches."""
    q = (animation or "").strip().lower()
    if not q:
        return None, None
    lib = _animation_library()

    if q.isdigit():
        for a in lib:
            if a.get("action_id") == int(q):
                return a["action_id"], a.get("name") or q
        return int(q), q

    def name_key(a):
        return str(a.get("name") or "").lower(), str(a.get("key") or "").lower()

    tests = (
        lambda n, k: q in (n, k),                         # exact
        lambda n, k: n.startswith(q) or k.startswith(q),  # prefix
        lambda n, k: q in n or q in k,                    # contains
    )
    for test in tests:
        for a in lib:
            n, k = name_key(a)
            if test(n, k):
                return a.get("action_id"), a.get("name") or q

    for word in re.findall(r"[a-z]+", q):
        category = ANIMATION_CATEGORY_HINTS.get(word)
        if category:
            for a in lib:
                if a.get("category") == category:
                    return a.get("action_id"), a.get("name") or q
    return None, None


def _glb_data_uri_input(safe: str) -> Dict[str, str]:
    glb = os.path.join(MODEL_DIR, f"{safe}.glb")
    if not os.path.exists(glb):
        raise RuntimeError(f"No model file '{safe}.glb' in {MODEL_DIR}")
    if os.path.getsize(glb) > 70 * 1024 * 1024:   # base64 grows ~33 %; Meshy allows 100 MB data URIs
        raise RuntimeError(f"'{safe}.glb' is too large to upload for rigging (>70 MB)")
    with open(glb, "rb") as f:
        b64 = base64.b64encode(f.read()).decode("ascii")
    return {"model_url": "data:application/octet-stream;base64," + b64}


def _create_rig(safe: str, job_key: str, height_m: float, p_to: int):
    """Rig the model. Prefers the textured refine task id; falls back to uploading the GLB."""
    meta = _load_meta(safe)
    body = {"height_meters": height_m}
    rig_id = None
    if meta.get("refine_task_id"):
        try:
            rig_id = _meshy_create(MESHY_RIG_URL, {"input_task_id": meta["refine_task_id"], **body})
        except RuntimeError as ex:
            print(f"[rig] input_task_id rejected ({ex}); uploading the GLB instead", flush=True)
    if rig_id is None:
        rig_id = _meshy_create(MESHY_RIG_URL, {**_glb_data_uri_input(safe), **body})
    task = _meshy_wait(MESHY_RIG_URL, rig_id, job_key, 0, p_to)
    _save_meta(safe, rig_task_id=rig_id, rig_expires_at=int(task.get("expires_at") or 0))
    return rig_id, task


def _rig_and_animate_background(safe: str, out_key: str, action_id: Optional[int], height_m: float):
    out_glb = os.path.join(MODEL_DIR, f"{out_key}.glb")
    try:
        if not MESHY_API_KEY:
            raise RuntimeError("MESHY_API_KEY not set.")

        # Reuse an earlier rig of this model while Meshy still keeps its files (10 min safety margin).
        meta = _load_meta(safe)
        rig_id = meta.get("rig_task_id") or None
        rig_reused = bool(rig_id) and int(meta.get("rig_expires_at") or 0) > (time.time() + 600) * 1000
        rig_task = None
        if not rig_reused:
            rig_id, rig_task = _create_rig(safe, out_key, height_m, p_to=50 if action_id is not None else 95)

        if action_id is None:
            if rig_task is None:
                resp = requests.get(f"{MESHY_RIG_URL}/{rig_id}", headers=_meshy_headers(), timeout=60)
                resp.raise_for_status()
                rig_task = resp.json()
            url = (rig_task.get("result") or {}).get("rigged_character_glb_url")
        else:
            p_from = 0 if rig_reused else 50
            try:
                anim_id = _meshy_create(MESHY_ANIM_URL, {"rig_task_id": rig_id, "action_id": action_id})
            except RuntimeError:
                if not rig_reused:
                    raise
                # The cached rig is no longer usable on Meshy's side: rig once more.
                rig_id, _ = _create_rig(safe, out_key, height_m, p_to=50)
                p_from = 50
                anim_id = _meshy_create(MESHY_ANIM_URL, {"rig_task_id": rig_id, "action_id": action_id})
            anim = _meshy_wait(MESHY_ANIM_URL, anim_id, out_key, p_from, 99)
            url = (anim.get("result") or {}).get("animation_glb_url")

        if not url:
            raise RuntimeError("Meshy returned no GLB url")
        _download_to_file(url, out_glb)
        anim_jobs[out_key] = {"status": "DONE", "progress": 100, "error": ""}
        print(f"[animate] done -> {out_glb}", flush=True)

    except Exception as e:
        msg = str(e)
        if "422" in msg or "pose" in msg.lower():
            msg += " (Rigging only works on humanoid, two-legged characters facing +Z.)"
        print("[animate] ERROR:", msg, flush=True)
        anim_jobs[out_key] = {"status": "ERROR", "progress": 0, "error": msg}


class AnimateModelRequest(BaseModel):
    name: str                              # existing generated model, e.g. Generated_Soldier
    animation: Optional[str] = ""          # "walk", "dance", "92" ... ; "" = rig only
    height_meters: Optional[float] = 1.7


@app.post("/api/animate-model")
def animate_model(req: AnimateModelRequest, background_tasks: BackgroundTasks):
    """Start (or poll) rigging + animation of an existing model. Call repeatedly until SUCCEEDED."""
    if not MESHY_API_KEY:
        return JSONResponse(status_code=500, content={"status": "FAILED", "progress": 0, "message": "MESHY_API_KEY missing."})

    safe = _safe_name(req.name)
    wanted = (req.animation or "").strip()
    try:
        action_id, action_name = _resolve_animation(wanted)
    except Exception as e:
        return JSONResponse(status_code=502, content={"status": "FAILED", "progress": 0,
                                                      "message": f"Could not load Meshy animation library: {e}"})
    if wanted and action_id is None:
        return JSONResponse(status_code=404, content={"status": "FAILED", "progress": 0,
                                                      "message": f"No Meshy animation matches '{wanted}'. See /api/animations?search=..."})

    out_key = f"{safe}__{_safe_name(action_name) if action_id is not None else 'rigged'}"
    glb_filename = f"{out_key}.glb"

    if os.path.exists(os.path.join(MODEL_DIR, glb_filename)):
        return JSONResponse(status_code=200, content={
            "status": "SUCCEEDED", "progress": 100, "animation": action_name or "rigged",
            "downloadUrl": f"{PUBLIC_BASE_URL}/files/{glb_filename}",
        })

    job = anim_jobs.get(out_key)
    if job and job.get("status") == "RUNNING":
        return JSONResponse(status_code=202, content={"status": "IN_PROGRESS", "progress": min(int(job.get("progress", 0)), 99)})
    if job and job.get("status") == "ERROR":
        anim_jobs.pop(out_key, None)   # allow a fresh retry next time
        return JSONResponse(status_code=500, content={"status": "FAILED", "progress": 0, "message": job.get("error", "Unknown error")})

    if not os.path.exists(os.path.join(MODEL_DIR, f"{safe}.glb")):
        return JSONResponse(status_code=404, content={"status": "FAILED", "progress": 0,
                                                      "message": f"'{req.name}' is not a generated model on this server."})

    print(f"[animate] start model={safe} animation={action_name or 'rig only'} (id={action_id})", flush=True)
    anim_jobs[out_key] = {"status": "RUNNING", "progress": 0, "error": ""}
    background_tasks.add_task(_rig_and_animate_background, safe, out_key, action_id, float(req.height_meters or 1.7))
    return JSONResponse(status_code=202, content={"status": "PENDING", "progress": 0})


@app.get("/api/animations")
def list_animations(search: str = "", category: str = ""):
    """Browse Meshy's animation library (free), e.g. /api/animations?search=dance or ?category=Dancing"""
    try:
        lib = _animation_library()
    except Exception as e:
        return JSONResponse(status_code=502, content={"error": str(e)})
    q = search.strip().lower()
    out = []
    for a in lib:
        if q and q not in str(a.get("name") or "").lower() and q not in str(a.get("key") or "").lower():
            continue
        if category and a.get("category") != category:
            continue
        out.append({k: a.get(k) for k in ("action_id", "name", "category", "sub_category", "preview_url")})
    return out

# =========================
# POSTER IMAGE ENDPOINT
# =========================

class PosterImageRequest(BaseModel):
    prompt: str
    width_px: int = 1024
    height_px: int = 1024


@app.post("/api/poster-image")
def poster_image(req: PosterImageRequest):
    prompt = (req.prompt or "").strip() or "A minimal poster"
    w = max(256, min(2048, int(req.width_px or 1024)))
    h = max(256, min(2048, int(req.height_px or 1024)))

    ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    filename = f"poster_{ts}.png"
    out_path = os.path.join(POSTER_DIR, filename)

    try:
        _make_ai_poster(prompt, out_path, w, h)
    except Exception as e:
        print(f"[poster] AI generation failed: {e}, using placeholder", flush=True)
        _make_placeholder_poster(prompt, out_path, w, h)

    return JSONResponse(content={"image_url": f"{PUBLIC_BASE_URL}/posters/{filename}"})

# =========================
# TEXTURE IMAGE ENDPOINT
# =========================

class TextureImageRequest(BaseModel):
    prompt: str
    size_px: int = 1024


@app.post("/api/texture-image")
def texture_image(req: TextureImageRequest):
    prompt = (req.prompt or "").strip() or "abstract pattern"
    size_px = max(256, min(1024, int(req.size_px or 1024)))

    ts = datetime.now().strftime("%Y%m%d_%H%M%S_%f")
    filename = f"texture_{ts}.png"
    out_path = os.path.join(TEXTURE_DIR, filename)

    _make_ai_texture(prompt, out_path, size_px=size_px)
    return JSONResponse(content={"image_url": f"{PUBLIC_BASE_URL}/textures/{filename}"})

# =========================
# LLM DECISION ENGINE
# =========================

def extract_json(text: str) -> dict:
    # Strip markdown fences
    text = re.sub(r"```(?:json)?", "", text).strip("`").strip()

    # Direct parse
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass

    # Find the outermost {...} block — handles leading/trailing explanation text
    start = text.find("{")
    end   = text.rfind("}")
    if start != -1 and end != -1 and end > start:
        try:
            return json.loads(text[start:end + 1])
        except json.JSONDecodeError:
            pass

    print(f"[extract_json] Could not parse LLM output:\n{text}\n", flush=True)
    return {"commands": [{"action": "no_action", "reason": "LLM returned unparseable output"}]}


def _commands_to_sentence(commands: list) -> str:
    """
    Converts commands into a short confirmation sentence for Unity.
    """
    if not commands:
        return ""

    parts = []
    for cmd in commands:
        action = cmd.get("action", "no_action")
        targets = cmd.get("targets") or []
        refs = cmd.get("reference_objects") or []

        primary = None
        if targets:
            primary = targets[0]
        elif refs:
            primary = refs[0]
        else:
            primary = "the object"

        if action == "run_code":
            behaviour = cmd.get("behaviour_prompt", "custom behaviour")
            short = behaviour[:90] + "..." if len(behaviour) > 90 else behaviour
            parts.append(f"Run script on {primary}: {short}")

        elif action == "generate_model":
            prompt = cmd.get("prompt", "object")
            parts.append(f"Generate a 3D model of '{prompt}'")

        elif action == "create_poster":
            prompt = cmd.get("image_prompt") or cmd.get("prompt", "image")
            parts.append(f"Create a poster: '{prompt}'")

        elif action == "set_wall_texture":
            prompt = cmd.get("texture_prompt", "texture")
            parts.append(f"Apply texture '{prompt}' to {primary}")

        elif action == "no_action":
            pass

        else:
            parts.append(f"Execute '{action}' on {primary}")

    return ". ".join(parts) + "." if parts else ""


def _print_llm_debug(transcript: str, gaze_target: str, commands: list, reasoning: str = "", vision_context: str = ""):
    if not VERBOSE_LOGGING:
        return
    print("\n========== XR PIPELINE DEBUG ==========")
    print()
    print(f"Transcript: {transcript}")
    print()
    print(f"Gaze Target: {gaze_target}")
    print()
    if vision_context:
        print(f"Vision Context: {vision_context}")
        print()

    if not commands:
        print("Intent: no_action")
        print()
        print("Behaviour Prompt: N/A")
        print()
        print("Reasoning: No commands returned.")
        print()
        print("=======================================\n")
        return

    for i, cmd in enumerate(commands, start=1):
        action = cmd.get("action", "no_action")
        targets = cmd.get("targets", [])
        reference_objects = cmd.get("reference_objects", [])
        relation = cmd.get("relation", None)
        behaviour_prompt = cmd.get("behaviour_prompt", "N/A")
        reason = cmd.get("reason", "")

        print(f"--- Command {i} ---")
        print()
        print(f"Intent (action): {action}")
        print()
        print(f"Targets: {targets}")
        print()
        print(f"Reference Objects: {reference_objects}")
        print()
        if relation is not None:
            print(f"Relation: {relation}")
            print()
        if action == "generate_model":
            print(f"Prompt: {cmd.get('prompt', '')}")
            print()
        elif action == "create_poster":
            print(f"Image Prompt: {cmd.get('image_prompt', '')}")
            print()
        elif action == "set_wall_texture":
            print(f"Texture Prompt: {cmd.get('texture_prompt', '')}")
            print()
        elif action == "set_dimensions":
            print(
                f"Dimensions: width_m={cmd.get('width_m')} "
                f"height_m={cmd.get('height_m')} depth_m={cmd.get('depth_m')}"
            )
            print()
        elif action == "scale":
            print(f"Scale Factor: {cmd.get('factor')}")
            print()
        else:
            print(f"Behaviour Prompt: {behaviour_prompt}")
            print()
        if reason:
            print(f"Reason: {reason}")
            print()

    if reasoning:
        print(f"Confirmation Summary: {reasoning}")
        print()

    print("=======================================\n")


def _extract_likely_nouns(text: str) -> list[str]:
    """Small helper to keep scene-name filtering deterministic and cheap."""
    text = (text or "").lower()
    noun_aliases = {
        "cube": ["cube"],
        "cubes": ["cube"],
        "tree": ["tree"],
        "trees": ["tree"],
        "wall": ["wall"],
        "walls": ["wall"],
        "floor": ["floor", "ground", "terrain", "sidewalk"],
        "ground": ["floor", "ground", "terrain", "sidewalk"],
        "stairs": ["stairs", "stair"],
        "stair": ["stairs", "stair"],
        "table": ["table"],
        "chair": ["chair"],
        "building": ["building", "house", "home", "wall"],
        "house": ["house", "home", "building", "Grote"],
        "home": ["house", "home", "building", "Grote"],
    }
    keys: list[str] = []
    for word, aliases in noun_aliases.items():
        if re.search(rf"\b{re.escape(word)}\b", text):
            keys.extend(aliases)
    # Always keep common grounding/surface names available.
    keys.extend(["floor", "ground"])
    # Preserve order, remove duplicates.
    seen = set()
    out = []
    for k in keys:
        kl = k.lower()
        if kl not in seen:
            seen.add(kl)
            out.append(k)
    return out


def filter_scene_objects_for_llm(scene_object_names: list[str], transcript: str, vision_context: str = "", gaze_target: str = "none", limit: int = 80) -> list[str]:
    """
    Avoid sending hundreds of scene names to the command LLM.
    Keeps exact names mentioned by gaze/vision plus names related to transcript nouns.
    """
    if not scene_object_names:
        return []

    names = [str(n) for n in scene_object_names if str(n).strip()]
    selected: list[str] = []

    def add(name: str):
        if name and name in names and name not in selected:
            selected.append(name)

    # Keep gaze target when meaningful.
    if gaze_target and gaze_target.lower() not in {"none", "floor", "ground", "wall", "ceiling", "terrain"}:
        add(gaze_target)

    # Keep exact scene names that appear in the structured/narrative vision text.
    vc = vision_context or ""
    for n in names:
        if n and n in vc:
            add(n)

    # Keep names matching likely transcript nouns.
    keys = _extract_likely_nouns(transcript + " " + vc)
    for key in keys:
        kl = key.lower()
        for n in names:
            if kl in n.lower():
                add(n)
                if len(selected) >= limit:
                    return selected

    # Keep a few common Unity primitive names because commands often use them.
    for n in names:
        nl = n.lower()
        if re.match(r"^cube(?:[._ -]?\d+)?$", nl) or nl in {"floor", "ground", "terrain"}:
            add(n)
            if len(selected) >= limit:
                return selected

    # Fallback: if we selected too little, include the first scene names, but cap hard.
    for n in names:
        add(n)
        if len(selected) >= min(limit, 40 if len(selected) < 5 else limit):
            break

    return selected[:limit]


def vision_describe(screenshots_b64: list[str], transcript: str, scene_object_names: list[str] = None) -> str:
    """
    Calls GPT-4o with screenshots and asks for structured visual grounding JSON.
    The command LLM can then use exact object names instead of parsing a prose description.
    """
    if not screenshots_b64:
        return ""
    screenshots_b64 = screenshots_b64[-2:]   # last 2 frames are enough for grounding

    try:
        content = []
        for b64 in screenshots_b64:
            content.append({
                "type": "image_url",
                "image_url": {
                    "url": f"data:image/jpeg;base64,{b64}",
                    "detail": "low",
                },
            })

        scene_names_str = ""
        if scene_object_names:
            # Vision gets the full list because it needs to match visual objects to exact Unity names.
            scene_names_str = (
                f"\n\nExact Unity GameObject names available in the scene:\n"
                f"{json.dumps(scene_object_names)}\n"
                f"Use exact names from this list when confident."
            )

        content.append({
            "type": "text",
            "text": (
                f"The user said: {json.dumps(transcript)}.\n"
                f"These {len(screenshots_b64)} screenshot(s) were captured during the user's command in a Unity XR scene.\n"
                f"Return ONLY valid JSON. No markdown. No explanation.\n"
                f"Schema:\n"
                f"{{\n"
                f"  \"visible_objects\": [{{\"name\": \"exact Unity name or unknown\", \"visual_label\": \"short label\", \"position\": \"left/right/center/foreground/background\", \"confidence\": 0.0}}],\n"
                f"  \"primary_references\": [\"exact object names most likely meant by this/that/these/two/etc\"],\n"
                f"  \"relations\": [{{\"relation\": \"between/near/on_top/left_of/right_of/visible_pair/etc\", \"objects\": [\"exact names\"], \"confidence\": 0.0}}],\n"
                f"  \"notes\": \"one short sentence, max 25 words\"\n"
                f"}}\n"
                f"Important grounding rules:\n"
                f"- For 'these two cubes', identify TWO separate cube-like objects if visible.\n"
                f"- If exact names are available, prefer names like Cube and Cube.001 over generic labels.\n"
                f"- Ignore UI buttons unless the user is clearly referring to UI.\n"
                f"- If only one object is visible, say so in notes and primary_references.\n"
                f"- Do not suggest actions. Only describe visual grounding."
                + scene_names_str
            ),
        })

        resp = client.chat.completions.create(
            model=OPENAI_CFG["vision_model"],
            response_format={"type": "json_object"},
            max_completion_tokens=OPENAI_CFG["vision_max_tokens"],
            reasoning_effort=OPENAI_CFG.get("vision_reasoning_effort", "low"),
            messages=[{"role": "user", "content": content}],
        )
        return resp.choices[0].message.content.strip()
    except Exception as e:
        print(f"[Vision error]: {e}", flush=True)
        return ""



def _rewrite_resize_commands(commands: list, transcript: str, gaze_target: str = "none") -> list:
    """
    Safety net: if the LLM accidentally returns run_code for a resize request,
    convert it into deterministic scale/set_dimensions before Unity sees it.
    """
    if not isinstance(commands, list):
        return commands

    text = (transcript or "").strip().lower()
    resize_words = (
        "resize", "scale", "size", "bigger", "larger", "smaller", "shrink",
        "width", "height", "depth", "wide", "high", "tall", "meter", "metre",
        " x ", " by "
    )

    if not any(w in text for w in resize_words):
        return commands

    def target_for(cmd):
        targets = cmd.get("targets") if isinstance(cmd, dict) else None
        if isinstance(targets, list) and targets:
            return targets[0]
        if gaze_target and gaze_target.lower() != "none":
            return gaze_target
        return None

    # Explicit WxH: "3 by 3", "5x5", "5 x 5"
    pair = re.search(
        r'(\d+(?:\.\d+)?)\s*(?:m(?:eters?|etres?)?\s*)?(?:x|×|by)\s*'
        r'(\d+(?:\.\d+)?)\s*(?:m(?:eters?|etres?)?)?',
        text
    )

    # Absolute single dimensions: "... height ... to 3 meters"
    height = re.search(
        r'(?:height|high|tall)\D{0,30}?(?:to|=|is|of)?\s*(\d+(?:\.\d+)?)\s*(?:m|meter|meters|metre|metres)?',
        text
    )
    width = re.search(
        r'(?:width|wide)\D{0,30}?(?:to|=|is|of)?\s*(\d+(?:\.\d+)?)\s*(?:m|meter|meters|metre|metres)?',
        text
    )
    depth = re.search(
        r'(?:depth|deep)\D{0,30}?(?:to|=|is|of)?\s*(\d+(?:\.\d+)?)\s*(?:m|meter|meters|metre|metres)?',
        text
    )

    # Proportional expressions.
    factor = None
    if re.search(r'\b(?:double|twice|2x)\b', text):
        factor = 2.0
    elif re.search(r'\b(?:half|half-size|half size)\b', text):
        factor = 0.5
    elif re.search(r'\bslightly\s+(?:bigger|larger)\b', text):
        factor = 1.2
    elif re.search(r'\b(?:bigger|larger|grow)\b', text):
        factor = 1.5
    elif re.search(r'\bslightly\s+(?:smaller|shrink)\b', text):
        factor = 0.8
    elif re.search(r'\b(?:smaller|shrink)\b', text):
        factor = 0.6

    rewritten = []
    for cmd in commands:
        if not isinstance(cmd, dict):
            rewritten.append(cmd)
            continue

        action = (cmd.get("action") or "").strip().lower()

        # Preserve already-correct deterministic commands.
        if action in {"scale", "set_dimensions"}:
            rewritten.append(cmd)
            continue

        # Only rewrite action commands that might otherwise mutate scale via generated code.
        if action != "run_code":
            rewritten.append(cmd)
            continue

        target = target_for(cmd)
        targets = [target] if target else []

        if pair:
            rewritten.append({
                "action": "set_dimensions",
                "targets": targets,
                "width_m": float(pair.group(1)),
                "height_m": float(pair.group(2)),
            })
            continue

        dims = {}
        if width:
            dims["width_m"] = float(width.group(1))
        if height:
            dims["height_m"] = float(height.group(1))
        if depth:
            dims["depth_m"] = float(depth.group(1))

        if dims:
            rewritten.append({
                "action": "set_dimensions",
                "targets": targets,
                **dims,
            })
            continue

        if factor is not None:
            rewritten.append({
                "action": "scale",
                "targets": targets,
                "factor": factor,
            })
            continue

        # Resize language was detected but values were not parseable.
        # Keep the original rather than guessing.
        rewritten.append(cmd)

    return rewritten


# Optional GPT-generated behavior for every scene manipulation, including resizing.
# Appended after the legacy examples so the latest instruction takes precedence.
if AI_CONFIG.get("behavior", {}).get("generate_code_for_all_commands", False):
    SYSTEM_PROMPT += """
OVERRIDE: CODE-ONLY SCENE MANIPULATION MODE (supersedes earlier scale/set_dimensions rules and examples):
For ALL scene changes (including scale, dimensions, move, rotate, change color, delete,
spawn primitives, effects, placement and procedural motion such as spinning or bobbing),
always return action=run_code.
Never return scale or set_dimensions in this mode.
Generate_model/create_poster/set_wall_texture still handle AI ASSET GENERATION.
animate_model still handles skeletal animation of humanoid characters (walk, run, dance, wave...).
For resize requests, put exact factors/metres and axis preservation in behaviour_prompt.
Respect selected poster isolation: modify only the selected poster transform, never its parent.
Examples: "make this chair bigger" -> run_code targets=["Chair"] with behaviour_prompt="In Start(), multiply only this.transform.localScale by 1.5 once."
"poster 3x2 meters" -> run_code on the selected Poster object, adjust only its own transform to requested world-space dimensions using RuntimeBounds, preserve thickness, never modify parent.
"move the cube 2 meters right" -> run_code on Cube, one-time relative translation in Start().
For commands with multiple actions, output separate run_code commands where appropriate.
"""

def llm_decide(transcript: str, gaze_target: str = "none", vision_context: str = "", scene_object_names: list = None) -> dict:
    if not transcript:
        return {"commands": [{"action": "no_action", "reason": "empty transcript"}]}

    filtered_scene_objects = filter_scene_objects_for_llm(
        scene_object_names or [],
        transcript=transcript,
        vision_context=vision_context,
        gaze_target=gaze_target,
        limit=80,
    )
    if scene_object_names:
        vprint(f"[Scene filter] Sent {len(filtered_scene_objects)} of {len(scene_object_names)} scene object names to command LLM", flush=True)

    user_message = f'GAZE_TARGET: "{gaze_target}"\nUSER SAID: "{transcript}"'
    if vision_context:
        user_message += f'\nVISION_CONTEXT_JSON: {vision_context}'
    if filtered_scene_objects:
        user_message += (
            f'\nSCENE_OBJECTS_FILTERED: {json.dumps(filtered_scene_objects)}'
            f'\nCRITICAL: You MUST use only exact names from SCENE_OBJECTS_FILTERED in "targets" and "reference_objects", except for generated object names created by your own generate_model command. '
            f'Never invent or paraphrase existing object names. Match the user\'s words and VISION_CONTEXT_JSON to the closest exact name in SCENE_OBJECTS_FILTERED.'
        )

    try:
        response = client.chat.completions.create(
            model=LLM_MODEL,
            response_format={"type": "json_object"},
            messages=[
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": user_message}
            ],
            max_completion_tokens=OPENAI_CFG["command_max_completion_tokens"],
            reasoning_effort=OPENAI_CFG.get("command_reasoning_effort", "low"),
        )

        raw = response.choices[0].message.content.strip()
        parsed = extract_json(raw)

        commands = parsed.get("commands", [])
        rewritten = commands if AI_CONFIG.get("behavior", {}).get("generate_code_for_all_commands", False) else _rewrite_resize_commands(commands, transcript, gaze_target)
        if rewritten != commands:
            vprint("[Resize safety] Rewrote LLM resize command:", json.dumps(rewritten), flush=True)
        parsed["commands"] = rewritten

        reasoning = _commands_to_sentence(parsed.get("commands", []))
        return {**parsed, "_reasoning": reasoning}

    except Exception as e:
        print(f"[LLM error]: {e}")
        return {"commands": [{"action": "no_action", "reason": f"LLM error: {str(e)}"}]}

# =========================
# TRANSCRIBE-ONLY ENDPOINT
# =========================

@app.post("/transcribe-only")
async def transcribe_only(
    audio: UploadFile = File(...),
    gaze_target: Optional[str] = Form(default="none"),
):
    """
    Step 1 of the two-step pipeline.
    Runs Whisper on the audio and returns the transcript immediately,
    without calling the LLM.
    """
    audio_bytes = await audio.read()

    gaze_target = gaze_target.strip() if gaze_target else "none"

    t0 = time.time()
    transcript = transcribe_audio_bytes(audio_bytes)
    whisper_time = round(time.time() - t0, 3)
    print(f"[Whisper-only] ({whisper_time}s): '{transcript}'")

    return JSONResponse(content={
        "transcript": transcript,
        "gaze_target": gaze_target,
        "whisper_ms": int(whisper_time * 1000),
    })

# =========================
# TRANSCRIBE ENDPOINT
# =========================

def _is_actionable(commands: list) -> bool:
    return any(cmd.get("action", "no_action") != "no_action" for cmd in commands)


def _clean_short_text(value: Any, max_len: int = 60) -> str:
    """Small helper for VR dialog labels: one line, readable, not too long."""
    s = str(value or "").strip()
    s = re.sub(r"\s+", " ", s)
    if len(s) > max_len:
        s = s[: max_len - 3].rstrip() + "..."
    return s


def _first_target(cmd: dict, fallback: str = "") -> str:
    """Return the most useful target name for a dialog summary."""
    if not isinstance(cmd, dict):
        return fallback or ""

    targets = cmd.get("targets")
    if isinstance(targets, list) and targets:
        return str(targets[0] or "").strip()

    target = str(cmd.get("target") or "").strip()
    if target:
        return target

    if fallback and fallback.lower() not in ("none", "null"):
        return fallback

    return ""


def _quoted_target(target: str) -> str:
    target = _clean_short_text(target, 80)
    return f' on "{target}"' if target else ""


def _colour_from_prompt(prompt: str) -> str:
    p = (prompt or "").lower()
    colours = [
        "black", "white", "red", "green", "blue", "yellow", "orange", "purple",
        "pink", "brown", "grey", "gray", "gold", "silver", "cyan", "magenta"
    ]
    for c in colours:
        if re.search(rf"\b{re.escape(c)}\b", p):
            return "grey" if c == "gray" else c
    return ""


def _build_command_summary(cmd: dict, gaze_target: str = "") -> str:
    """
    Builds the short confirmation text shown in Unity.
    Examples:
      Change color to black on "Cube"
      Generate poster of football fans on "Wall_01"
      Generate texture of bricks on "Building_01"
    """
    if not isinstance(cmd, dict):
        return "Execute action"

    action = (cmd.get("action") or "no_action").strip().lower()
    target = _first_target(cmd, gaze_target)
    target_suffix = _quoted_target(target)

    if action == "create_poster":
        subject = _clean_short_text(cmd.get("image_prompt") or "poster", 70)
        return f"Generate poster of {subject}{target_suffix}"

    if action == "set_wall_texture":
        subject = _clean_short_text(cmd.get("texture_prompt") or "texture", 70)
        return f"Generate texture of {subject}{target_suffix}"

    if action == "generate_model":
        subject = _clean_short_text(cmd.get("prompt") or cmd.get("name") or "3D model", 70)
        return f"Generate 3D model of {subject}"

    if action == "animate_model":
        anim = _clean_short_text(cmd.get("animation") or "add skeleton", 40)
        return f'Animate "{target}": {anim}' if target else f"Animate model: {anim}"

    if action == "run_code":
        behaviour = str(cmd.get("behaviour_prompt") or "")
        low = behaviour.lower()

        # 1) Delete: check first so it is never mislabelled as move/colour
        if re.search(r"\b(delete|destroy|remove)\b", low):
            return f'Delete "{target}"' if target else "Delete object"

        if re.search(r"\b(particles?|particlesystem|fire|flames?|smoke|water|fountain|sparks?|explosion|mist|waterfall)\b", low):
            return f"Create visual effect{target_suffix}"

        colour = _colour_from_prompt(behaviour)
        if colour and re.search(r"\b(colou?r|turn|paint|change|make|set)\b", low):
            return f'Change color of "{target}" to {colour}' if target else f"Change color to {colour}"

        if re.search(r"\b(rotate|rotation|spin)\b", low):
            return f"Rotate{target_suffix}"
        if re.search(r"\b(move|position|place|put)\b", low):
            return f"Move{target_suffix}"

        return _clean_short_text(f"Action{target_suffix}: {behaviour}", 60)

    if action == "set_dimensions":
        w = cmd.get("width_m")
        h = cmd.get("height_m")
        d = cmd.get("depth_m")
        parts = []
        if w is not None: parts.append(f"width {w} m")
        if h is not None: parts.append(f"height {h} m")
        if d is not None: parts.append(f"depth {d} m")
        detail = ", ".join(parts)
        return f"Resize{target_suffix}" + (f" to {detail}" if detail else "")

    if action == "scale":
        f = cmd.get("factor")
        try:
            f = float(f)
        except (TypeError, ValueError):
            f = None
        if f is not None and f < 1:
            return f"Shrink{target_suffix}"
        if f is not None and f > 1:
            return f"Enlarge{target_suffix}"
        return f"Resize{target_suffix}"

    if action == "no_action":
        return "No action"

    return f"Execute {action.replace('_', ' ')}{target_suffix}"


def _build_dialog_summary(commands: list, gaze_target: str = "") -> str:
    actionable = [c for c in (commands or []) if isinstance(c, dict) and c.get("action") != "no_action"]
    if not actionable:
        return "No action"

    summaries = [_build_command_summary(c, gaze_target) for c in actionable]

    # Usually there is one action. For multi-step commands, keep it compact.
    if len(summaries) == 1:
        return summaries[0]

    joined = " + ".join(summaries)
    return _clean_short_text(joined, 150)


def _build_confirmation_message(commands: list, reasoning: str) -> str:
    # Keep the old long message available for logs/backward compatibility.
    if reasoning:
        return reasoning
    actions = [cmd.get("action", "no_action") for cmd in commands]
    return f"Execute the following action(s): {', '.join(actions)}?"



# Live GPT transcription: Unity -> FastAPI HTTP chunks -> OpenAI WebSocket.
# One upstream socket per VR user/recording. Keys never leave the Python server.
class LiveSession:
    def __init__(self, socket):
        self.socket = socket
        self.final_parts = []
        self.partial = ""
        self.errors = []
        self.lock = asyncio.Lock()
        self.reader = None

live_sessions = {}

async def _live_reader(s):
    try:
        async for raw in s.socket:
            msg = json.loads(raw)
            typ = msg.get("type", "")
            if typ == "conversation.item.input_audio_transcription.delta":
                s.partial += msg.get("delta", "")
            elif typ == "conversation.item.input_audio_transcription.completed":
                text = msg.get("transcript", "").strip()
                if text:
                    s.final_parts.append(text)
                s.partial = ""
            elif typ == "error":
                s.errors.append(str(msg.get("error", msg)))
    except Exception as exc:
        s.errors.append(str(exc))

@app.post("/api/live/start")
async def live_start():
    if not SPEECH_CFG.get("streaming_enabled", False):
        raise HTTPException(409, "Enable speech.streaming_enabled in ai_models.json")
    if not OPENAI_API_KEY:
        raise HTTPException(500, "OPENAI_API_KEY not configured")
    try:
        ws = await websockets.connect(
            "wss://api.openai.com/v1/realtime?intent=transcription",
            additional_headers={"Authorization": "Bearer " + OPENAI_API_KEY},
            max_size=2 ** 22,
        )
        await ws.send(json.dumps({"type":"session.update","session":{
            "type":"transcription", "audio":{"input":{
                "format":{"type":"audio/pcm","rate":24000},
                "transcription": {
                    "model":SPEECH_CFG.get("streaming_model","gpt-live-transcribe"),
                    "delay":SPEECH_CFG.get("delay","low"),
                    "prompt":SPEECH_CFG.get("context_prompt","Voice commands to edit objects in a Unity XR environment"),
                }, "turn_detection":None}}}}))
        sid=str(_uuid.uuid4())
        s=LiveSession(ws)
        live_sessions[sid]=s
        s.reader=asyncio.create_task(_live_reader(s))
        return {"session_id":sid}
    except Exception as exc:
        raise HTTPException(502, f"Live connection failed: {exc}")

@app.post("/api/live/{sid}/audio")
async def live_audio(sid: str, audio: bytes = Body(..., media_type="application/octet-stream")):
    s=live_sessions.get(sid)
    if s is None: raise HTTPException(404, "Unknown live session")
    if len(audio)>262144: raise HTTPException(413, "Chunk too large")
    if len(audio)%2: raise HTTPException(400, "PCM16 requires even byte count")
    async with s.lock:
        await s.socket.send(json.dumps({"type":"input_audio_buffer.append","audio":base64.b64encode(audio).decode("ascii")}))
    return {"ok":True}

@app.get("/api/live/{sid}/status")
async def live_status(sid: str):
    s=live_sessions.get(sid)
    if s is None: raise HTTPException(404,"Unknown live session")
    return {"partial":s.partial,"final":" ".join(s.final_parts),"errors":s.errors[-2:]}

@app.post("/api/live/{sid}/finish")
async def live_finish(sid: str):
    s=live_sessions.pop(sid,None)
    if s is None: raise HTTPException(404,"Unknown live session")
    try:
        async with s.lock:
            await s.socket.send(json.dumps({"type":"input_audio_buffer.commit"}))
        for _ in range(40):
            await asyncio.sleep(.1)
            if s.errors or s.final_parts: break
        return {"transcript":" ".join(s.final_parts).strip(),"errors":s.errors[-2:]}
    finally:
        await s.socket.close()
        if s.reader: s.reader.cancel()


@app.post("/transcribe")
async def transcribe(
    audio: Optional[UploadFile] = File(default=None),
    transcript_override: Optional[str] = Form(default=None),
    gaze_target: Optional[str] = Form(default="none"),
    screenshots_b64: Optional[str] = Form(default=""),
    scene_objects: Optional[str] = Form(default=""),
):
    """
    Full pipeline: Whisper -> (optional) GPT-4o Vision (multi-frame) -> LLM -> confirmation gate.
    Returns stage-level latency metadata to Unity.
    """
    request_started = time.perf_counter()

    audio_bytes = await audio.read() if audio is not None else b""

    gaze_target       = gaze_target.strip() if gaze_target else "none"
    screenshots_raw   = (screenshots_b64 or "").strip()
    scene_objects_raw = (scene_objects or "").strip()
    vprint(f"\n[Gaze target]: '{gaze_target}'\n")

    # Parse the JSON array of base64 strings sent from Unity.
    screenshots_list = []
    if screenshots_raw:
        try:
            screenshots_list = json.loads(screenshots_raw)
            vprint(f"[Vision] Received {len(screenshots_list)} screenshot(s)\n")
        except Exception as e:
            print(f"[Vision] Failed to parse screenshots array: {e}", flush=True)

    # Parse scene object names sent from Unity.
    scene_object_names = []
    if scene_objects_raw:
        try:
            scene_object_names = json.loads(scene_objects_raw)
            vprint(f"[Scene] Received {len(scene_object_names)} scene object names\n")
        except Exception as e:
            print(f"[Scene] Failed to parse scene_objects: {e}", flush=True)

    t0 = time.perf_counter()
    # Blocking OpenAI/Whisper calls run in a worker thread so other users' requests are not queued.
    transcript = (transcript_override.strip() if transcript_override
                  else await asyncio.to_thread(transcribe_audio_bytes, audio_bytes))
    whisper_time = time.perf_counter() - t0
    vprint(f"[Whisper] ({whisper_time:.3f}s): '{transcript}'\n")

    # Vision pass (only if screenshots were sent).
    vision_context = ""
    vision_time = 0.0
    if screenshots_list:
        # Send vision only the relevant subset of scene names, not the full list.
        vision_names = filter_scene_objects_for_llm(
            scene_object_names, transcript, "", gaze_target, limit=120)
        tv = time.perf_counter()
        vision_context = await asyncio.to_thread(
            vision_describe, screenshots_list, transcript, vision_names)
        vision_time = time.perf_counter() - tv
        vprint(f"[Vision] ({vision_time:.3f}s): '{vision_context}'\n")

    t1 = time.perf_counter()
    result = await asyncio.to_thread(
        llm_decide,
        transcript,
        gaze_target=gaze_target,
        vision_context=vision_context,
        scene_object_names=scene_object_names,
    )
    llm_time = time.perf_counter() - t1

    commands = result.get("commands", [{"action": "no_action"}]) or [{"action": "no_action"}]
    reasoning = result.get("_reasoning", "")

    vprint(f"\n[LLM] ({llm_time:.3f}s) -> {json.dumps(commands)}\n")
    if reasoning:
        vprint(f"[Reasoning] {reasoning}\n")

    _print_llm_debug(transcript, gaze_target, commands, reasoning, vision_context)

    # Total server time includes request parsing / audio save / orchestration overhead.
    server_total_time = time.perf_counter() - request_started
    whisper_ms = int(round(whisper_time * 1000))
    vision_ms = int(round(vision_time * 1000))
    llm_ms = int(round(llm_time * 1000))
    server_total_ms = int(round(server_total_time * 1000))
    server_overhead_ms = max(0, server_total_ms - whisper_ms - vision_ms - llm_ms)

    log_entry = {
        "time": datetime.now().isoformat(),
        "transcript": transcript,
        "gaze_target": gaze_target,
        "vision_context": vision_context,
        "screenshot_count": len(screenshots_list),
        "scene_objects": scene_object_names,
        "commands": commands,
        "whisper_ms": whisper_ms,
        "vision_ms": vision_ms,
        "llm_ms": llm_ms,
        "server_overhead_ms": server_overhead_ms,
        "server_total_ms": server_total_ms,
    }
    with open(TRANSCRIPT_FILE, "a", encoding="utf-8") as f:
        f.write(json.dumps(log_entry) + "\n")

    meta = {
        "whisper_ms": whisper_ms,
        "vision_ms": vision_ms,
        "llm_ms": llm_ms,
        "server_overhead_ms": server_overhead_ms,
        "server_total_ms": server_total_ms,
        "model": LLM_MODEL,
    }

    primary = commands[0] if commands else {"action": "no_action"}
    primary_action = primary.get("action", "no_action")
    primary_target = (
        (primary.get("targets") or [None])[0]
        or primary.get("target")
        or gaze_target
        or "none"
    )
    print(
        f"[CMD] {transcript!r} | action={primary_action} | target={primary_target} | "
        f"STT={whisper_ms}ms | Vision={vision_ms}ms | LLM={llm_ms}ms | Server={server_total_ms}ms\n",
        flush=True,
    )

    if _is_actionable(commands):
        session_id = str(_uuid.uuid4())
        confirmation_message = _build_confirmation_message(commands, reasoning)
        dialog_summary = _build_dialog_summary(commands, gaze_target)

        pending_commands[session_id] = {
            "commands": commands,
            "transcript": transcript,
            "gaze_target": gaze_target,
            "created_at": time.time(),
            "dialog_summary": dialog_summary,
        }

        vprint(f"[Pending] session={session_id} summary='{dialog_summary}' msg='{confirmation_message}'")

        return JSONResponse(content={
            "transcript": transcript,
            "gaze_target": gaze_target,
            "requires_confirmation": True,
            "session_id": session_id,
            "confirmation_message": confirmation_message,
            "dialog_summary": dialog_summary,
            "commands": commands,
            "command": commands[0] if commands else {"action": "no_action"},
            "meta": meta,
        })

    return JSONResponse(content={
        "transcript": transcript,
        "gaze_target": gaze_target,
        "requires_confirmation": False,
        "commands": commands,
        "command": commands[0],
        "meta": meta,
    })

# =========================
# EXECUTE ENDPOINT
# =========================

class ConfirmRequest(BaseModel):
    session_id: str


@app.post("/execute")
def execute(req: ConfirmRequest):
    entry = pending_commands.pop(req.session_id, None)
    if entry is None:
        return JSONResponse(
            status_code=404,
            content={"error": "session_id not found or already consumed"},
        )

    commands = entry["commands"]
    vprint(f"[Execute] session={req.session_id} -> {json.dumps(commands)}")

    return JSONResponse(content={
        "status": "executed",
        "session_id": req.session_id,
        "commands": commands,
        "command": commands[0],
        "transcript": entry.get("transcript", ""),
        "gaze_target": entry.get("gaze_target", "none"),
    })

# =========================
# CANCEL ENDPOINT
# =========================

@app.post("/cancel")
def cancel(req: ConfirmRequest):
    discarded = pending_commands.pop(req.session_id, None)
    if discarded:
        vprint(f"[Cancel] session={req.session_id} discarded.")
        return JSONResponse(content={"status": "cancelled", "session_id": req.session_id})
    return JSONResponse(
        status_code=404,
        content={"error": "session_id not found or already consumed"},
    )

# =========================
# HEALTH CHECK
# =========================

@app.get("/api/config/models")
async def public_model_config():
    # Public model identifiers and tuning only: NEVER expose API keys or .env values.
    # Re-read ai_models.json on every request so a changed code_model reaches Unity
    # without restarting the server (fall back to the startup values if the file is invalid).
    try:
        cfg = load_ai_config()["openai"]
    except Exception as ex:
        print(f"[config] Could not re-read {CONFIG_PATH}: {ex}; using startup values", flush=True)
        cfg = OPENAI_CFG
    return {"openai": {
        "code_model": cfg["code_model"],
        "code_max_tokens": cfg.get("code_max_tokens", 3500),
        "code_temperature": cfg.get("code_temperature", 0.2),
        "code_reasoning_effort": cfg.get("code_reasoning_effort", "low")
    }}

@app.get("/health")
async def health():
    return {
        "status": "ok",
        "model": LLM_MODEL,
        "whisper": SPEECH_CFG["model"],
        "meshy": bool(MESHY_API_KEY),
        "openai_images": bool(OPENAI_API_KEY),
    }

# =========================
# RUN
# =========================

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000, access_log=False)