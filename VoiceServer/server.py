# =========================
# LLM-DRIVEN XR SERVER
# No keywords — pure natural language -> LLM -> Unity commands
# Includes: Meshy.ai 3D generation, OpenAI image/texture generation
# =========================

from fastapi import FastAPI, File, Form, UploadFile, BackgroundTasks
from fastapi.responses import JSONResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel
from typing import Optional, Dict, Any, List
from PIL import Image, ImageDraw
import os, json, re, time, base64, io, socket
from datetime import datetime
from faster_whisper import WhisperModel
from openai import OpenAI
from dotenv import load_dotenv
import requests
import uuid as _uuid

load_dotenv()

app = FastAPI()

# =========================
# CONFIG
# =========================
OPENAI_API_KEY = os.getenv("OPENAI_API_KEY")
client = OpenAI(api_key=OPENAI_API_KEY)
print("OPENAI_API_KEY loaded:", bool(OPENAI_API_KEY))

# "o4-mini" is good for structured JSON
LLM_MODEL = "o4-mini"


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
MESHY_TEXT2_3D_CREATE = f"{MESHY_BASE}/openapi/v2/text-to-3d"
MESHY_TEXT2_3D_GET = f"{MESHY_BASE}/openapi/v2/text-to-3d"

# In-memory job tracker for background Meshy tasks
jobs: Dict[str, Dict[str, Any]] = {}

# In-memory store for commands awaiting user confirmation
pending_commands: Dict[str, Dict[str, Any]] = {}

# =========================
# WHISPER
# =========================
print("Loading Whisper model...")
whisper_model = WhisperModel("small", device="cpu", compute_type="int8")
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
     movement, rotation, scaling, color changes, duplication-like behaviours, particles,
     animation, interaction, effects, procedural logic, following, constraints, stacking,
     placement, lighting, deletion, spawning primitives, physics, UI, audio, and any
     other Unity behaviour whatsoever.
   - If in doubt, use run_code.
   - Required fields:
     - "targets": array of zero or more exact GameObject names
     - "reference_objects": array of zero or more exact GameObject names
     - "relation": optional relation string or null
     - "behaviour_prompt": concise but implementation-oriented instruction for generated Unity C# code

5. no_action
   - Use when the input is purely conversational, unclear, or no XR action should happen.

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
- Use generate_model ONLY for generating new 3D objects/models from text.
- Use create_poster ONLY for poster/image generation.
- Use set_wall_texture ONLY for texture generation/application.
- For EVERY other actionable XR instruction, use run_code.
- NEVER return translate, scale, set_color, rotate, delete_object, duplicate_object, spawn_primitive, set_lighting, or any action name not listed above.
- Output ONLY raw JSON.

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

User: "make a poster of a snowy mountain landscape"
{"commands":[{"action":"create_poster","image_prompt":"snowy mountain landscape","width_m":1.5,"height_m":1.0}]}

User: "apply brick texture to the wall"
{"commands":[{"action":"set_wall_texture","texture_prompt":"old red brick wall","target":"Wall"}]}

User: "create a realistic wooden chair"
{"commands":[{"action":"generate_model","prompt":"realistic wooden chair","name":"Wooden_Chair","stage":"preview","art_style":"realistic"}]}

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
{"commands":[{"action":"generate_model","prompt":"tree","name":"Generated_Tree","stage":"preview","art_style":"realistic"},{"action":"run_code","targets":["Generated_Tree"],"reference_objects":["Cube","Cube.001"],"relation":"between","behaviour_prompt":"on Start, place Generated_Tree at the midpoint between Cube and Cube.001 using combined renderer bounds, then ground it on the nearest floor surface while preserving its scale and rotation"}]}

User: "place a cube between these two cubes"
VISION_CONTEXT: {"primary_references":["Cube","Cube.001"]}
{"commands":[{"action":"run_code","targets":[],"reference_objects":["Cube","Cube.001"],"relation":"between","behaviour_prompt":"on Start, create a new Unity cube primitive named Generated_Cube at the midpoint between Cube and Cube.001 using renderer bounds, set scale to Vector3.one, and ground it on the nearest floor surface"}]}

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


def _set_job_progress(safe: str, stage: str, task_id: str, meshy_status: str, progress: Any):
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

    print(f"[meshy] {phase} {task_id} status={status} progress={p}", flush=True)


def _generate_with_meshy_background(prompt: str, name: str, stage: str, art_style: str):
    safe = _safe_name(name)
    out_glb = os.path.join(MODEL_DIR, f"{safe}.glb")

    try:
        if not MESHY_API_KEY:
            raise RuntimeError("MESHY_API_KEY not set.")

        jobs[safe] = {
            "status": "RUNNING", "stage": stage, "task_id": None,
            "meshy_status": "PENDING", "progress": 0, "error": "",
        }

        preview_task_id = _meshy_create_task(
            mode="preview",
            payload={"prompt": prompt, "art_style": art_style, "should_remesh": True},
        )
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
            time.sleep(3)

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
            payload={"preview_task_id": preview_task_id, "enable_pbr": True},
        )
        _set_job_progress(safe, stage, refine_task_id, "PENDING", 0)

        refine_task = None
        deadline = time.time() + 30 * 60
        while time.time() < deadline:
            t = _meshy_get_task(refine_task_id)
            meshy_status = t.get("status") or "PENDING"
            progress = t.get("progress") or 0
            _set_job_progress(safe, stage, refine_task_id, meshy_status, progress)
            _log_meshy_progress_once(safe, "refine", refine_task_id, meshy_status, progress)
            if meshy_status == "SUCCEEDED" and t.get("model_urls", {}).get("glb"):
                refine_task = t
                break
            if meshy_status in ("FAILED", "CANCELED"):
                raise RuntimeError(f"Meshy refine failed: {t.get('task_error') or t}")
            time.sleep(3)

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
        model="gpt-image-1",
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
        model="gpt-image-1",
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
    stage: Optional[str] = "preview"
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
        return JSONResponse(status_code=202, content={
            "status": job.get("meshy_status", "IN_PROGRESS"),
            "progress": int(job.get("progress", 0) or 0),
        })

    if job and job.get("status") == "ERROR":
        return JSONResponse(status_code=500, content={
            "status": "FAILED", "progress": 0,
            "message": job.get("error", "Unknown error"),
        })

    stage = (req.stage or "preview").lower()
    art_style = (req.art_style or "realistic").lower()
    print(f"[text-to-3d] starting name={req.name} safe={safe} stage={stage} art_style={art_style}", flush=True)

    jobs[safe] = {
        "status": "RUNNING", "stage": stage, "task_id": None,
        "meshy_status": "PENDING", "progress": 0, "error": "",
    }

    background_tasks.add_task(_generate_with_meshy_background, req.prompt, req.name, stage, art_style)
    return JSONResponse(status_code=202, content={"status": "PENDING", "progress": 0})

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
            model="gpt-4o",
            response_format={"type": "json_object"},
            max_tokens=700,
            messages=[{"role": "user", "content": content}],
        )
        return resp.choices[0].message.content.strip()
    except Exception as e:
        print(f"[Vision error]: {e}", flush=True)
        return ""


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
        print(f"[Scene filter] Sent {len(filtered_scene_objects)} of {len(scene_object_names)} scene object names to command LLM", flush=True)

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
            max_completion_tokens=3000,
        )

        raw = response.choices[0].message.content.strip()
        parsed = extract_json(raw)
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
    with open(TEMP_AUDIO_PATH, "wb") as f:
        f.write(audio_bytes)

    gaze_target = gaze_target.strip() if gaze_target else "none"

    t0 = time.time()
    segments, _info = whisper_model.transcribe(TEMP_AUDIO_PATH, beam_size=5, task="translate")
    transcript = "".join(s.text for s in segments).strip()
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


def _build_confirmation_message(commands: list, reasoning: str) -> str:
    if reasoning:
        return reasoning
    actions = [cmd.get("action", "no_action") for cmd in commands]
    return f"Execute the following action(s): {', '.join(actions)}?"


@app.post("/transcribe")
async def transcribe(
    audio: UploadFile = File(...),
    gaze_target: Optional[str] = Form(default="none"),
    screenshots_b64: Optional[str] = Form(default=""),
    scene_objects: Optional[str] = Form(default=""),
):
    """
    Full pipeline: Whisper -> (optional) GPT-4o Vision (multi-frame) -> LLM -> confirmation gate.
    """
    with open(TEMP_AUDIO_PATH, "wb") as f:
        f.write(await audio.read())

    gaze_target     = gaze_target.strip() if gaze_target else "none"
    screenshots_raw = (screenshots_b64 or "").strip()
    scene_objects_raw = (scene_objects or "").strip()
    print(f"\n[Gaze target]: '{gaze_target}'\n")

    # Parse the JSON array of base64 strings sent from Unity
    screenshots_list = []
    if screenshots_raw:
        try:
            screenshots_list = json.loads(screenshots_raw)
            print(f"[Vision] Received {len(screenshots_list)} screenshot(s)\n")
        except Exception as e:
            print(f"[Vision] Failed to parse screenshots array: {e}\n")

    # Parse scene object names sent from Unity
    scene_object_names = []
    if scene_objects_raw:
        try:
            scene_object_names = json.loads(scene_objects_raw)
            print(f"[Scene] Received {len(scene_object_names)} scene object names\n")
        except Exception as e:
            print(f"[Scene] Failed to parse scene_objects: {e}\n")

    t0 = time.time()
    segments, _info = whisper_model.transcribe(TEMP_AUDIO_PATH, beam_size=5, task="translate")
    transcript = "".join(s.text for s in segments).strip()
    whisper_time = round(time.time() - t0, 3)
    print(f"[Whisper] ({whisper_time}s): '{transcript}'\n")

    # ── Vision pass (only if screenshots were sent) ───────────────────────────
    vision_context = ""
    if screenshots_list:
        tv = time.time()
        vision_context = vision_describe(screenshots_list, transcript, scene_object_names)
        vision_time = round(time.time() - tv, 3)
        print(f"[Vision] ({vision_time}s): '{vision_context}'\n")

    t1 = time.time()
    result = llm_decide(transcript, gaze_target=gaze_target, vision_context=vision_context, scene_object_names=scene_object_names)
    llm_time = round(time.time() - t1, 3)

    commands = result.get("commands", [{"action": "no_action"}]) or [{"action": "no_action"}]
    reasoning = result.get("_reasoning", "")

    print(f"\n[LLM] ({llm_time}s) -> {json.dumps(commands)}\n")
    if reasoning:
        print(f"[Reasoning] {reasoning}\n")

    _print_llm_debug(transcript, gaze_target, commands, reasoning, vision_context)

    log_entry = {
        "time": datetime.now().isoformat(),
        "transcript": transcript,
        "gaze_target": gaze_target,
        "vision_context": vision_context,
        "screenshot_count": len(screenshots_list),
        "scene_objects": scene_object_names,
        "commands": commands,
        "whisper_ms": int(whisper_time * 1000),
        "llm_ms": int(llm_time * 1000),
    }
    with open(TRANSCRIPT_FILE, "a", encoding="utf-8") as f:
        f.write(json.dumps(log_entry) + "\n")

    meta = {
        "whisper_ms": log_entry["whisper_ms"],
        "llm_ms": log_entry["llm_ms"],
        "model": LLM_MODEL,
    }

    if _is_actionable(commands):
        session_id = str(_uuid.uuid4())
        confirmation_message = _build_confirmation_message(commands, reasoning)

        pending_commands[session_id] = {
            "commands": commands,
            "transcript": transcript,
            "gaze_target": gaze_target,
            "created_at": time.time(),
        }

        print(f"[Pending] session={session_id}  msg='{confirmation_message}'\n")

        return JSONResponse(content={
            "transcript": transcript,
            "gaze_target": gaze_target,
            "requires_confirmation": True,
            "session_id": session_id,
            "confirmation_message": confirmation_message,
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
    print(f"[Execute] session={req.session_id} -> {json.dumps(commands)}\n")

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
        print(f"[Cancel] session={req.session_id} discarded.")
        return JSONResponse(content={"status": "cancelled", "session_id": req.session_id})
    return JSONResponse(
        status_code=404,
        content={"error": "session_id not found or already consumed"},
    )

# =========================
# HEALTH CHECK
# =========================

@app.get("/health")
async def health():
    return {
        "status": "ok",
        "model": LLM_MODEL,
        "whisper": "small",
        "meshy": bool(MESHY_API_KEY),
        "openai_images": bool(OPENAI_API_KEY),
    }

# =========================
# RUN
# =========================

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000, access_log=False)