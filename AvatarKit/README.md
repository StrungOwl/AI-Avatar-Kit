# AI Avatar Kit — student starter scripts

Drop-in scripts so you spend your time on **your character**, not on plumbing.

Nothing in here is specific to any one character. Your character lives in a
**Persona asset** you create in the Inspector — you should never need to open a
`.cs` file to change who they are. If you find yourself wanting to, tell me;
that's a bug in this kit.

---

## Setup (about 10 minutes)

### 1. Copy the kit in
Drag the `AvatarKit` folder into your Unity project's `Assets` folder.

### 2. Add your API key
Create a plain text file called **`avatar_key.txt`** next to your `Assets`
folder (NOT inside it) containing nothing but your OpenAI key.

```
YourUnityProject/
├── Assets/
├── Packages/
└── avatar_key.txt     <-- here
```

Then add `avatar_key.txt` to your `.gitignore`.

(Also accepted: one folder **above** the project — outside the repo entirely,
so it can never be committed, and one key serves several projects. The Health
Check prints both accepted paths.)

> **Why outside `Assets`?** Anything you type into a component gets saved into
> the scene file, and scene files get committed to git. **A key in a scene is a
> key on GitHub.** Keys also must never ship inside a mobile or web build —
> anything in an app can be extracted. Desktop/Editor only for now.

### 3. Get your character into the scene
Your model needs, at minimum:
- **A rigged skeleton with standard bone names** (Mixamo / Avaturn / Ready Player Me: Hips, Spine, Head, LeftArm …). **FBX:** select the model file → Rig → Animation Type → **Humanoid** → Apply. **glTF/.glb:** nothing to set — the setup command builds the Humanoid rig for you (glTF files arrive with no Animator).
- **No rig at all?** (a static mesh) Still fine: voice, brain, memory, knowledge and the speech bubble all work. You only lose head/body motion and the mouth.
- **Blendshapes** for the mouth (at least one "jaw open" shape) — *nice to have*. Without them she can't open her mouth, so the kit nods her head in time with her voice instead. It still reads as talking.

No model yet? [avaturn.me](https://avaturn.me) makes a rigged avatar from a
selfie. Pick a **T2** body if you want mouth blendshapes (T1 bodies have a static
face). It downloads as `.glb`, which Unity needs one free package to read:
**Package Manager → ＋ → Install package by name → `com.unity.cloud.gltfast`**.
Model not rigged at all ("missing Hips" / "Bones I could not find")?
Auto-rig it free at mixamo.com — body only, it adds no face shapes.

### 4. Wire it up
Select your character in the Hierarchy, then:

**Tools → AI Avatar → Set Up Selected Character**

That adds and connects everything. Then run **Tools → AI Avatar → Health Check** —
it tells you in plain language what's still missing.

### 5. Create your character
Right-click in the Project window → **Create → AI Avatar → Character Persona**.

Fill it in, then drag it onto the **AvatarBrain** component.

### 6. Press Play
Your character greets you. **Hold SPACE** (or the on-screen button), talk,
release. Talking while they're speaking interrupts them.

---

## What each script does

| Script | Job |
|---|---|
| `CharacterPersona.cs` | **Your character, as an asset.** Name, greeting, system prompt, voice, model settings. |
| `AvatarVoiceAPI.cs` | The three web requests: transcribe, chat, speak (OpenAI or ElevenLabs). Plus key loading. |
| `AvatarVoice.cs` | **Her voice.** A dropdown: OpenAI or ElevenLabs. Paste a Voice ID for any ElevenLabs voice, including a clone. The Inspector shows where the key file goes and whether it found it. |
| `Editor/AvatarVoiceEditor.cs` | The AvatarVoice Inspector: shows only the boxes for the provider you picked, plus the key-file status and a *Make the key file for me* button. |
| `AvatarWav.cs` | AudioClip ⇄ WAV bytes, both directions. |
| `AvatarBrain.cs` | The conversation loop, short-term memory, and on-screen UI. |
| `AvatarMemory.cs` | **Long-term memory** — she remembers you across sessions, in a text file you can open. The enable checkbox is the privacy toggle. |
| `AvatarKnowledge.cs` | **Vetted knowledge (RAG)** — she answers facts from your `Assets/Knowledge` documents, not from guesses. |
| `AvatarLipSync.cs` | Turns audio into mouth movement. |
| `AvatarBody.cs` | Blinking, head look-at, breathing, gestures, the talking head-nod. Sets your Animator Controller's `Talking` / `Listening` bools if they exist. |
| `AvatarSpeechBubble.cs` | *Optional.* A speech bubble by her head that reveals her words one by one as she speaks. Add it by hand: **Add Component → Avatar Speech Bubble**. Works on every model, mouth or not. |
| `Editor/AvatarSetupWizard.cs` | The one-click setup and health check. Picks the mesh with the most blendshapes as the face. |
| `Editor/GltfHumanoidBuilder.cs` | Builds a Humanoid Avatar + Animator from bone names for glTF characters (**Tools → AI Avatar → Make Selected Character Humanoid (glTF)**; the setup command calls it automatically). |
| `Editor/KnowledgeIndexer.cs` | **Tools → AI Avatar → Build Knowledge Index** — chunks + embeds your knowledge files into `knowledge.json`. |
| `KnowledgeSample/` | Example knowledge files to copy into `Assets/Knowledge` — including the deliberate booby trap. |

---

## The mental model

An AI avatar is **four web requests and a wave file**:

```
hold key → mic → WAV → [transcribe] → text
                                        ↓
                    [embed] → nearest knowledge chunks ─┐
                                        ↓               ↓
                 [chat + persona + memory + chunks + history] → reply
                                        ↓
                                    [speak] → WAV → AudioSource
                                                        ↓
                                              loudness → mouth
```

Those boxes are somebody else's server, plus a bit of maths on your machine.
That's the whole trick — and everything the kit adds (persona, memory,
knowledge) is the **same move made three times: choosing text to put in the
context window** before the model answers. The industry calls that job
*context engineering*.

**Where does the memory live?** Two places, and the difference is worth
understanding:

- **Short-term** — a list in RAM inside `AvatarBrain`. It vanishes when you
  press Stop. The illusion of a continuous conversation comes from
  **re-sending the whole transcript with every request.**
- **Long-term** — `AvatarMemory` journals every exchange to disk instantly,
  then a background call distils it into `{yourcharacter}_memory.md` next to
  the project (same rule as the key file: visible, editable, gitignored — add
  `*_memory.md` and `*_pending.txt` to `.gitignore`). Those notes ride the
  system prompt on every call, and at launch she **greets you from what she
  remembers.** Memory is not a database — it's string concatenation into the
  context window.

The honest caveat, worth saying in any demo: the memory files live on your
machine and nowhere else — delete them and she has genuinely forgotten. But
every conversation turn is still **processed** by the model provider's API.
Where memory is *stored* is your choice; where words are *processed* is theirs.
That's why the `AvatarMemory` enable checkbox exists: unticked, she keeps
nothing, and you have an anonymous companion.

---

## Making it yours

Everything below is on the Persona asset or the components — no code:

| You want | Change |
|---|---|
| A different personality | `systemPrompt` on the Persona |
| A different voice | `AvatarVoice` → **Provider** dropdown → pick a voice (see *Change the voice* below) |
| An ElevenLabs or cloned voice | `AvatarVoice` → Provider **ElevenLabs** → paste the **Voice ID** |
| How the voice is *delivered* | `AvatarVoice` → `voiceDirection` (an acting note, not words — OpenAI only) |
| Shorter / longer replies | `maxTokens`, and the "keep replies short" rule in the prompt |
| More/less unpredictable | `temperature` (0.8–0.9 suits most characters) |
| Longer short-term memory | `memoryTurns` (costs more per message) |
| Fresher long-term notes | `AvatarMemory` → `updateEveryExchanges` (smaller = more API calls) |
| She should forget someone | Right-click `AvatarMemory` → **Memory: Clear** (archives, never destroys) |
| An anonymous companion | Untick the `AvatarMemory` component — nothing is ever kept |
| Facts she must get right | Write them in `Assets/Knowledge/*.md`, rebuild the index, keep `useKnowledge` on |
| The hallucination A/B demo | Persona → `useKnowledge` off / on, same question |
| See what she retrieved | `AvatarBrain` → `showKnowledgePanel` (on-screen sources + scores) |
| Mouth moves too much/little | `AvatarLipSync` → `gain` |
| More/less head tracking | `AvatarBody` → `headLookWeight` |
| Another language | Say so in the system prompt: `"Hablas español."` |

### Change the voice

Select your character → find the **AvatarVoice** component → pick a **Provider**.

**OpenAI** (the default)
1. Pick a voice from the **Voice** list.
2. Nothing else to set up. It uses your `avatar_key.txt`.

**ElevenLabs** (a stock voice, a shared voice, or your own clone)
1. Make an ElevenLabs API key: elevenlabs.io → your profile → **API Keys** → Create. Turn on **only Text to Speech**.
2. Make a text file called **`elevenlabs_key.txt`** with only that key in it. Put it next to `avatar_key.txt`. (The Inspector shows the exact path. The **Make the key file for me** button creates it and opens it.)
3. Add `elevenlabs_key.txt` to your `.gitignore`.
4. Get the voice ID: elevenlabs.io → **Voices** → click the voice → copy its **ID**. A shared voice must be added to **My Voices** first.
5. Paste the ID into **Voice ID**. Press Play.

The Inspector box turns blue when it finds the key, red when it does not.
Only the *sound* changes — the words still come from the OpenAI brain, so you
still need `avatar_key.txt` too.

No AvatarVoice component? Then the kit uses `ttsVoice` on the Persona, as before.

---

## When it doesn't work

Run **Tools → AI Avatar → Health Check** first. Then:

**"Nothing happens and there's no error."**
The Editor throttles Play mode when its window isn't focused — coroutines stall
and web requests never finish, silently. The kit sets `Application.runInBackground`
at startup; make sure the Game view is actually focused too.

**The mouth doesn't move (but audio plays).**
Watch `Peak Open` on `AvatarLipSync` while your character talks. If it's under
~10, raise `gain`. **Measure it — don't guess.** A gain that's 12× too low still
"works", it's just invisible.

**The mouth is stuck wide open, or they're grinning permanently.**
Blendshapes import from FBX at weight **100**, not 0 — FBX stores a
`DeformPercent` per shape and exporters write 100. The kit zeroes them in
`Awake()`. If it's still happening, your shape names didn't match, so nothing
is driving them.

**"Could not find a jaw/open blendshape."**
The warning prints every blendshape your mesh actually has. Copy the right name
into `AvatarLipSync` → `Jaw Shape`. Until then she nods instead.

**"… has no blendshapes, so the mouth can't open. Falling back to a head nod."**
Not an error. glTF/Avaturn-T1/Mixamo-only characters have no face shapes; the
voice drives `AvatarBody → Talk Nod No Mouth` (degrees) instead. Want a mouth?
Re-export from Avaturn as a T2 body, or add shape keys in Blender.

**No head movement or gestures.**
Your rig isn't Humanoid. FBX: model file → Rig → Animation Type → Humanoid →
Apply. glTF: **Tools → AI Avatar → Make Selected Character Humanoid (glTF)**.

**She slowly twists into a pretzel while playing.**
Only possible if the Animator has no controller (no idle clip, typical for
glTF) and something multiplies bone rotations every frame. The kit caches the
rest pose and sets from it in that case; any Animator Controller with an idle
clip also cures it.

**They never hear me.**
Check `Microphone Index` on `AvatarBrain` — the Health Check lists every mic
with its number.

**Is it the API or the mic?**
Right-click `AvatarBrain` while playing → **Say Test Line**. If they speak, the
API is fine and the problem is your microphone.

**The greeting takes a moment on launch.**
That's the "remembering last time..." pass: any journal left over from your
previous session is being folded into her notes *before* she greets you, so
the greeting already knows. That pause is the price of never losing a word —
there is no trustworthy "on exit" moment to do it in.

**She "remembered" something wrong.**
Open `{yourcharacter}_memory.md` and read what was actually written — a
self-writing memory compounds its own garbage, so audit it. Fix or delete the
bad line, then right-click `AvatarMemory` → **Memory: Reload file from disk**.

---

## Ethics — read before you clone anything

- Build an original character, or a stylised version of **yourself**.
- **No classmates, no celebrities, no real people without consent.** This
  applies to faces *and* to voices.
- If you clone a voice, clone your own — and say on camera that you did.
