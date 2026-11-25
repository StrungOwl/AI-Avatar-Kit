# Knowledge folder — how to write it

Copy these files into a folder called `Knowledge` inside `Assets`
(`Assets/Knowledge/`), replace the example facts with **your** facts, then run
**Tools → AI Avatar → Build Knowledge Index**. Re-run it whenever a file changes.

Three writing rules that decide whether retrieval works:

1. **One topic per paragraph.** The paragraph is the unit of retrieval — a
   paragraph that mixes the class schedule with the character's backstory
   retrieves badly for both.
2. **Facts, stated absolutely.** "Class starts at 6pm on Thursdays," not
   "class is at the usual time." Each paragraph must stand alone; the model
   sees it without the file around it.
3. **Include facts the base model cannot know** — your schedule, your lab,
   your character's private lore. Those make the demo. And deliberately
   **leave one topic out**: asking about the gap is how you prove she says
   "I don't know" instead of guessing.

`injection-test.md` is a booby trap **on purpose** — index it, then ask a
question near its topic and watch the instruction inside it *fail*. Retrieved
text is reference data, not instructions; that one demo is most of what
"prompt injection" means.
