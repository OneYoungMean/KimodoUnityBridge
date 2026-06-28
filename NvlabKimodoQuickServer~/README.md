# NvlabKimodoQuickServer1

## Language
- Chinese: `README_ZH.md`
- English: `README.md`

## Features
- Build runtime environment with `uv` pipeline.
- Start Kimodo bridge server with model options.
- Run TCP example flow (`ping -> generate -> quit`).

## Requirements
- Windows 10/11 x64
- Model root available (recommended): `C:\nvlab\models~`
- `uv` is required. `run_server.bat` / `run_server.sh` can download an unmanaged local `uv` binary into `program\exe\uv\` on first launch if missing. Its package cache still uses uv's normal global cache location.

## Install
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
run_server.bat setup --output console
```

If you already have a baked FP16 text encoder at `C:\nvlab\LLMVec-GGUF\KIMODO-Meta3_llm2vec_FP16`, you can build the local CPU INT8 asset first:
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
program\exe\uv\uv.exe run --python 3.12 --no-project python tools\build_llm2vec_int8.py --verify
```

Linux:
```bash
cd /mnt/c/nvlab/NvlabKimodoQuickServer1
./run_server.sh setup --output console
```

## Example
```bat
cd /d C:\nvlab\NvlabKimodoQuickServer1
run_server.bat --model Kimodo-SOMA-RP-v1 --models-root C:\nvlab\models~ --output console
```

Linux:
```bash
cd /mnt/c/nvlab/NvlabKimodoQuickServer1
./run_server.sh --model Kimodo-SOMA-RP-v1 --models-root /mnt/c/nvlab/models~ --output console
```

Low-VRAM runs now default to the local Torch CPU INT8 text encoder asset under `models\KIMODO-Meta3_llm2vec_INT8`.

TCP smoke test:
```bat
example\example_run_server_tpose.bat
```

Live console variant:
```bat
example\example_run_server_tpose_console_live.bat
```

## Parameters
- See `PARAMETERS.md`
