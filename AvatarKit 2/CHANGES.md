# AvatarKit 2 -- what changed from AvatarKit 1, and why

## New parts

| Part | What it does | Why |
|---|---|---|
| `AvatarConnection` | Holds your Worker address and app password. Every link the kit calls is a box in the Inspector. | AvatarKit 1 read the API key from a text file beside the project. A phone or a web page has no such file, and a key packed into an app can be pulled out. Now the keys stay on your own small server (a Cloudflare Worker). |
| `AvatarLive` | Live talk (speech-to-speech) over one open line. Two providers: OpenAI Realtime, or an ElevenLabs agent (for a cloned voice). You can talk over her to cut in. | The three-call way always has a wait. One open line is much faster. |
| Notes in live talk | In live talk she has a "look in my notes" tool. She uses it only when a question needs a fact. Small talk costs nothing extra. (`AvatarLive` > Knowledge Notes) | In live talk your voice goes straight to the model, so the app can not add the notes first, the way the three-call way does. |
| Live talk on the web | `Plugins/WebGL/AvatarLiveWeb.jslib`. In a browser, this file opens the line, reads the microphone and plays her voice with the browser's own tools. | A browser does not let Unity do those three things from C#. |
| `AvatarActions` | Tasks. A list of things the avatar may ask the game to do (show a tutorial video, change scene, open a link, anything you can drag into a Unity event), and a list of facts the game knows (minutes played, friends invited). No code for simple cases. Works in Three Calls, Text and Live. | "Can the avatar DO something?" The avatar never does the task. It asks your game to do it. |
| Talk / Text chooser | A start screen: TALK (speak and listen) or TEXT (type, and read her reply in the speech bubble; no microphone, no sound). A small button changes mode while the app runs. | A person in a busy place can not talk out loud. |
| "Which key" read-out | The top-right corner shows which key each call uses, as it happens. | It makes the hidden part of an AI avatar visible. |
| `LOOK AND WORDS` on AvatarBrain | Every colour, size, font and word of the UI is a box in the Inspector. | No code needed to restyle. |

## Fixes

| Fault in AvatarKit 1 | Fix |
|---|---|
| No API key in a phone or web build. | Keys on the Worker (`AvatarConnection`, Key Home = Worker). The key file still works in the Editor (Key Home = Key File). |
| No microphone on Android, iPhone or the web. | The app now asks for the microphone at the start. The setup command also fills in the iPhone "why we need the microphone" text. |
| Web build: no sound was recorded. | A browser can only read a recording after it stops. The kit now waits for the sound before it sends it. |
| The talk button did nothing on a phone. | On a phone a press is a touch, and an OnGUI button never hears it. Presses are now read from the Input System. |
| Knowledge notes were missing in a build. | The index is now written to `Assets/Resources/knowledge.json`, which Unity packs inside the app. Run `Tools > AI Avatar > Build Knowledge Index` one time. |
| The UI was tiny on a phone. | The UI grows on sharp screens and keeps clear of the notch. |
| Newer models refused `temperature` or `max_tokens`. | Two switches on the Persona: `Send Temperature`, `New Token Limit Name`. |
| With no microphone the app stopped. | It now starts in Text mode. |

## Changed files

- New: `Runtime/AvatarConnection.cs`, `Runtime/AvatarLive.cs`, `Runtime/AvatarActions.cs`, `Plugins/WebGL/AvatarLiveWeb.jslib`, `Editor/AvatarAgentNotesTool.cs`, `CHANGES.md`
- Rewritten: `Runtime/AvatarBrain.cs`
- Edited: `Runtime/AvatarVoiceAPI.cs`, `Runtime/AvatarVoice.cs`, `Runtime/AvatarKnowledge.cs`, `Runtime/AvatarSpeechBubble.cs`, `Runtime/AvatarLipSync.cs`, `Runtime/CharacterPersona.cs`, `Editor/AvatarSetupWizard.cs`, `Editor/KnowledgeIndexer.cs`, `Editor/AvatarVoiceEditor.cs`

## How to move a project from AvatarKit 1 to AvatarKit 2

1. Close Unity. Replace the folder `Assets/AvatarKit` with this one.
2. Open Unity. Select your character. Run `Tools > AI Avatar > Set Up Selected Character`. It adds `AvatarConnection` and `AvatarLive`.
3. On `AvatarConnection`, paste your Worker address and app password.
4. Run `Tools > AI Avatar > Build Knowledge Index` one time.
5. Run `Tools > AI Avatar > Health Check`.

## Known limits

- In a web build, live talk uses the browser's own echo removal instead of the kit's cut-in settings, and the mouth moves by loudness only.
- Live talk with an ElevenLabs agent uses the kit's knowledge notes only after the agent has a client tool named `look_in_notes`. Add it one time: `Tools > AI Avatar > Add Notes And Actions To My ElevenLabs Agent` (one click), or by hand on elevenlabs.io (agent > Tools > Add tool > Client tool; name `look_in_notes`; one text parameter `question`; 'Wait for response' on).
- In a web build the three-call way is push-to-talk only.

## Phone builds (added for Demo 2)

- New tool: `Tools > AI Avatar > Get Ready For A Phone Build` (`Editor/AvatarPhoneCheck.cs`).
  It puts the open avatar scene first in the build list, sets Android Internet Access to
  Require, checks for a 64-bit Android build (IL2CPP + ARM64), sets the iPhone microphone
  sentence, checks that the keys are on the Worker, and warns when the app still has the
  Unity template name. It asks before it changes a setting.
- Tested: an Android `.apk` build and an iPhone Xcode project export from a copy of the
  class project (Unity 6000.5.5f1). Not tested here: the Xcode step on a Mac.
- `Avatar Live > Cut In Loudness` now goes up to 15 (was 4). A phone speaker is next to the phone microphone, so a phone needs a higher number than a computer.
