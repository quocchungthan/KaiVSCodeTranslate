# Kai VS Code Translate

Turn English technical-book PDFs into faithful Vietnamese books — Markdown + PDF — using an agent-driven, fully local pipeline inside VS Code.

*Biến sách kỹ thuật tiếng Anh (PDF) thành sách tiếng Việt — Markdown + PDF — bằng một quy trình chạy hoàn toàn cục bộ trong VS Code.*

---

## The story / Câu chuyện

Good engineering books are almost always written in English. For many Vietnamese developers the bottleneck is not motivation, it is the 300 pages of dense English standing between them and the idea. Machine translation of a whole book usually breaks the things that make a technical book useful: headings drift, code blocks get "translated", diagrams disappear, terminology changes from chapter to chapter, and the run dies on page 140 with no way to resume.

This repository is the answer to that. It is a durable, resumable translation pipeline where an agent (**Huong**) drives real tools instead of pasting a book into a chat box:

- **Nothing leaves the machine.** PDFs, extracted text, images, and translations stay local. No upload without explicit approval.
- **State is authoritative.** Every job lives in `_processing/jobs/<job-id>/job.json`. Interrupt it at chunk 12 of 19 and it resumes at chunk 12.
- **Structure is preserved.** Headings, lists, tables, code, equations, captions, links, and image order survive the trip.
- **Terminology is continuous.** Each chunk hands off a glossary and formatting decisions to the next one, so "consistent hashing" reads the same on page 20 and page 250.
- **Quality is gated.** A job is only "done" after heading parity, asset resolution, text-extractable Vietnamese output, and human inspection of first/middle/last pages.

*Sách kỹ thuật hay hầu hết đều viết bằng tiếng Anh. Repo này giúp dịch trọn cuốn sang tiếng Việt mà vẫn giữ nguyên cấu trúc, mã nguồn, hình ảnh và thuật ngữ — chạy cục bộ, có thể dừng và chạy tiếp bất cứ lúc nào.*

---

## Who is this for / Dành cho ai

| You are... | What you get |
| --- | --- |
| A Vietnamese developer who learns faster in Vietnamese | A readable Vietnamese PDF/Markdown of the book you own |
| A study group or team lead | A consistent shared translation instead of 5 different ad-hoc ones |
| An agent/pipeline builder | A worked example of durable, resumable, skill-driven agent workflows |
| A visitor with no setup | [Open an issue](#request-a-translation--yêu-cầu-dịch-sách) and ask for a book |

Not for: pirating books. Translate material you legally own or that is openly licensed.

---

## How it works / Cách hoạt động

Five durable stages, driven by the `Huong` agent:

```
convert -> chunk -> translate -> assemble -> validate
```

| Stage | What happens |
| --- | --- |
| `convert` | PDF → structure-preserving Markdown, assets extracted, OCR only where needed |
| `chunk` | Markdown split into ordered semantic chunks (~18k chars, code-fence aware) |
| `translate` | Each chunk translated to Vietnamese, with glossary/context handoff |
| `assemble` | Chunks merged, assets wired, PDF rendered with Vietnamese-capable fonts |
| `validate` | Automated QC + visual inspection of representative pages |

Layout:

```
_pdfs/                      # your source PDFs (never modified, gitignored)
_processing/jobs/<job-id>/  # job.json state, chunks, per-chunk context
_output/<job-id>/           # book.vi.md, book.vi.pdf, assets/, qc.json
.github/tools/              # PowerShell + Python pipeline tools
.github/skills/             # the agent's stage playbooks
```

---

## How to use it yourself / Cách tự chạy

### Prerequisites

Nothing is installed for you — the agent probes first and asks before any download.

- Windows + PowerShell, VS Code with GitHub Copilot (agent mode)
- Python 3 for the extraction/preview tools in `.github/tools/`
- For rendering: Pandoc / Typst and Noto fonts (Vietnamese-capable). Pinned versions live in [.github/translation-pipeline.defaults.json](.github/translation-pipeline.defaults.json)

### Run

1. Drop your PDF into `_pdfs/`.
2. Open VS Code Chat and select the **Huong** agent.
3. Ask it to continue the pipeline. Or drive it manually:

```powershell
& .\.github\tools\Initialize-TranslationJobs.ps1
& .\.github\tools\Get-NextTranslationAction.ps1
```

4. Check progress any time:

```powershell
Get-Content .\_processing\jobs\<job-id>\job.json | ConvertFrom-Json | Select-Object status
```

5. Collect `_output/<job-id>/book.vi.md`, `book.vi.pdf`, and `qc.json`.

Sanity-check the pipeline itself (no external dependencies):

```powershell
& .\.github\tools\Test-TranslationPipeline.ps1
```

More detail: [.github/TRANSLATION_WORKFLOW.md](.github/TRANSLATION_WORKFLOW.md)

---

## Request a translation / Yêu cầu dịch sách

**You don't need to install anything.** If you want a book translated, open an issue and I'll run the pipeline locally and reply in the thread with the Vietnamese output next to the English source.

👉 **[Open a translation request issue](https://github.com/quocchungthan/KaiVSCodeTranslate/issues/new?template=translation-request.yml)**

In the issue, please include:

1. Book title, author, and edition
2. Why you want it in Vietnamese / who benefits
3. Confirmation you own a legal copy, or a link if it is openly licensed
4. Whether you want the whole book or specific chapters
5. Any terminology preferences (e.g. keep "load balancer" untranslated)

What happens next:

- I queue the request, run the pipeline on my machine, and reply in the issue.
- The reply includes the **Vietnamese version** and points at the **English source sections** it came from, so you can check anything that reads oddly.
- Long books are delivered chapter by chapter as chunks finish.
- Source PDFs are never committed or uploaded — only the translated output is shared, and only where licensing allows.

*Bạn không cần cài gì cả. Hãy mở một issue mô tả cuốn sách bạn muốn đọc bằng tiếng Việt. Tôi sẽ chạy pipeline trên máy mình và trả lời ngay trong issue đó với bản tiếng Việt kèm phần tiếng Anh gốc để đối chiếu.*

Other issues are welcome too: a bad translation you spotted, a broken image, a tool that failed, or an idea for a new stage.

---

## Ground rules / Nguyên tắc

- Files in `_pdfs/` are never modified.
- `_pdfs/`, `_processing/`, and `_output/` stay gitignored — no book content in git history.
- No source-book content is sent to external services without explicit authorization.
- Nothing is invented: unreadable source is recorded as an unresolved reference, not guessed.
