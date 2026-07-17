<!--
SPDX-FileCopyrightText: 2026 CodeFusion SpA
SPDX-FileCopyrightText: 2026 SharpEmu Emulator Project
SPDX-License-Identifier: Apache-2.0

Adapted for codefusion-repo/sharpemu on 2026-07-17.
-->

# AGENTS.md

AGENTS.md es el bootloader terminal de `codefusion-repo/sharpemu`. No es fuente de verdad
ni concede permisos: el comportamiento genérico vive en `project-os-es/kernel/`
y los hechos del target se reconstruyen desde su evidencia viva.

## Identidad del repositorio

Estas rutas y la versión adoptada son configuración de máquina/adopción, no
estado vivo. Conserva estos campos y orden.

PROJECT_NAME = SharpEmu
REPOSITORY_NAME = codefusion-repo/sharpemu
REPOSITORY_LOCAL_PATH = /workspace/sharpemu
DEFAULT_BRANCH = main
WORK_BRANCH_PATTERN = work/*
PM_FACING_LANGUAGE = es
KERNEL_REPOSITORY = codefusion-repo/project-os-v2
KERNEL_LOCAL_PATH = /workspace/project-os-v2/project-os-es/kernel
KERNEL_VERSION_ADOPTED = 716f0b4d320cb21257a482ce6699a1f5919e3783

## Resolución del kernel

Antes de trabajo no trivial, lee `project-os-es/kernel/manifest.json` y sigue
su `resolution_sequence`. En terminal, cuando el checkout del kernel esté
disponible, usa este fast path:

```sh
KERNEL_DIR="/workspace/project-os-v2/project-os-es/kernel"
PROJECT_OS_ROOT="${KERNEL_DIR%/project-os-es/kernel}"
python "$PROJECT_OS_ROOT/tools/project_os_resolve.py" \
  --actor <actor> --workflow <workflow> --mode <mode> \
  --kernel-dir "$KERNEL_DIR" [--skill skill.<id>]
```

El resolver acelera la resolución; el manifest sigue siendo canónico. Ambos
solo dan forma y nunca autorizan una acción. Consulta artefactos, templates y
skills desde las referencias resueltas, sin copiar sus contratos aquí. Usa
`context_plan` para distinguir archivos cargados internamente, metadata
proyectada, templates y skills referenciados; no lo trates como prueba del
contenido entregado al modelo.

Abre después de resolver solo el template aplicable, las skills solicitadas y
las fuentes Project OS, target o evidencia viva exigidas por scope, validación
o source basis. Reporta las lecturas reales en el recibo canónico de fuentes,
en el envelope del output junto al artefacto, con paths relativos al repositorio
o identificadores vivos y razones, nunca con cuerpos completos. Una lectura
adicional requiere una razón admitida por el contrato; no recorras
recursivamente Project OS por defecto.

## Evidencia viva

Reconstruye el estado desde GitHub, git, el roadmap canónico `#1`
y los ADRs del target cuando apliquen. No lo guardes en este archivo. Ante
kernel, evidencia, autoridad o validación requerida faltantes o ambiguos, falla
cerrado según el kernel resuelto.

## Notas propias del target

- SharpEmu se distribuye bajo GPL-2.0-or-later y todo archivo nuevo debe cumplir REUSE.
- No introduzcas código propietario de Sony, firmware, claves, assets descifrados, datos de juegos ni materiales PlayStation protegidos.
- Basa el reverse engineering en información pública, técnicas clean-room o investigación original.
- Mantén los cambios pequeños, mantenibles, explicables y validados.

La política genérica de seguridad, trazabilidad y validación sigue en
`project-os-es/docs/reglas.md` y el kernel resuelto.
