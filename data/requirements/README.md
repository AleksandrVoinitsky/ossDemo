# Каталог требований

`requirements-registry.jsonl` — машиночитаемая версия утверждённого реестра из папки разметки. Исходный XLSX не используется приложением во время работы.
`requirements-registry.manifest.json` фиксирует контрольную сумму исходника, число строк и исключённые коды.
`classifier-mapping.jsonl` содержит нормализованные правила связи карточки объекта с 49 критериями классификатора.
`inspector-checklist-template.jsonl` содержит 235 проверочных пунктов и оснований из утверждённого рабочего шаблона 2026 года.

Каталог пересобирается воспроизводимо:

```powershell
python ./scripts/build-requirements-catalog.py `
  "C:\OCC\разметка\Общий Реестр требований.xlsx" `
  ./data/requirements/requirements-registry.jsonl
```

Mapping пересобирается отдельно:

```powershell
python ./scripts/build-classifier-mapping.py `
  "C:\OCC\разметка\Mapping_Карточка_объекта_+_Классификатор.xlsx" `
  ./data/requirements/classifier-mapping.jsonl
```

Рабочий слой чек-листа пересобирается из проверенного исторического шаблона:

```powershell
python ./scripts/build-checklist-template.py `
  "C:\OCC\разметка\Исторические чек-листы\Вот ЛПУ Чек-лист ИК  ООС 2026.xlsx" `
  ./data/requirements/inspector-checklist-template.jsonl
```

Строки с одинаковыми основанием и формулировкой объединяются, а уровни требований, категории и коды классификатора сохраняются массивами. Несуществующий в классификаторе код `2.7` исключается только из привязок; сами требования не удаляются, поскольку во всех таких строках присутствуют другие действующие коды.
