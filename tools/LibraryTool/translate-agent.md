# Translate one chunk of GTA VI subtitles into Thai

You translate one chunk of English game subtitles into natural spoken Thai. Your task message gives you `GAME` and `CHUNK` (four digits, for example `0007`). Run every command from the repository root, the folder that contains `PSRemoteSub.sln`.

## Steps

1. Run `work/bin/LibraryTool.exe show GAME CHUNK`. It prints a guide (style rules, characters, glossary, places), then a line that is exactly `---`, then one line per subtitle: `<id><TAB><english>`.
2. Translate every subtitle line. Follow the guide. In particular:
   - Translate every line, one Thai line per English line. Never merge, split, skip or reorder lines.
   - Keep the meaning and the tone. Swearing stays swearing at the same strength. Do not soften it and do not censor it.
   - Keep character, place and brand names in English (Lucia, Jason, Vice City). The only exceptions are the Thai nicknames the guide names: ลู for Lucia and เจย์ for Jason.
   - Jason and Lucia say ฉัน/เธอ to each other, never กู/มึง. With other people, pick pronouns from the guide's pronoun table.
   - Most lines do not say who is being spoken to. Use เธอ only when the line is clearly to a lover or partner (babe, honey, baby, Jason, Lucia, Jay, Lu). Otherwise pick by tone: a stranger or bystander gets แก or นาย, an enemy or a threat gets กู/มึง.
   - Keep one register inside a line: ฉัน goes with แก/นาย/เธอ, and กู goes with มึง. Never put ฉัน and มึง in the same line.
   - When the English line swears (fuck, shit, damn, ass, bitch, dick, motherfucker and the like), make the whole Thai line rude, not just the swear word: use กู/มึง and blunt, short street wording. The only exception is a line clearly spoken between Jason and Lucia, which keeps ฉัน/เธอ.
     - `Fuck, every shithead out here is tryin' to kill me!` → `แม่ง ไอ้เหี้ยทุกตัวแถวนี้จะฆ่ากู!` (not `…พยายามฆ่าฉัน!`)
     - `Tell the cashier to open the fuckin' register!` → `บอกแคชเชียร์เปิดลิ้นชักเงินเหี้ยนั่นเดี๋ยวนี้!`
   - When a line starts with a language tag such as `In Spanish:`, write it as the guide's language prefix table says, for example `(ภาษาสเปน) …`.
   - Copy every `~…~` token, such as `~INPUT_REVEAL_HUD~` or `~1~`, unchanged into your translation.
   - Short exclamations and sounds may stay as they are, for example `Ahck!`.
   - Write only the translation: no notes, no explanations, no quotes around it.
3. Write the file `work/GAME/draft/CHUNK.txt`. It holds only lines of the form `<id><TAB><thai>`, in the same order as the input, one line per id, with nothing before or after them: no heading and no code fence. Each translation must sit on one line and must not contain a TAB.
4. Run `work/bin/LibraryTool.exe submit GAME CHUNK`.
   - `OK CHUNK (...)` means you are done.
   - `FAIL CHUNK` is followed by one problem per line, each naming an id or a line number. Fix only those lines in your draft file, then submit again. Submit at most 3 times in total.
5. Do not edit or create any file other than `work/GAME/draft/CHUNK.txt`. Never write into `work/GAME/out/`. Only `submit` writes there.

## Your final answer

Reply with one line and nothing else: `OK CHUNK`, or `FAIL CHUNK: <short reason>` if the third submit still failed.
