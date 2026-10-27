"""Edits of the files a start writes before KSP runs: the DLSS-G package's ini, the mod's settings.cfg and the proxy's
ini, and reading an ini back."""
import re


def set_package_ini_value(text, section, key, value):
    """The DLSS-G package's ini with section's key set to value: the key's line where the
    section has one, else a line after the section's last one, else the section at the end.
    The rest of the file, its comments and line endings stay."""
    ending = "\r\n" if "\r\n" in text else "\n"
    lines = text.splitlines(keepends=True)
    inside = False
    first = last = key_line = None
    for n, line in enumerate(lines):
        stripped = line.strip()
        header = re.match(r"^\[(.+)\]$", stripped)
        if header:
            if inside:
                break
            inside = header.group(1).strip().lower() == section.lower()
            if inside:
                first = last = n
            continue
        if not inside:
            continue
        if stripped:
            last = n
        m = re.match(r"^([^;#=][^=]*)=", stripped)
        if m and m.group(1).strip().lower() == key.lower():
            key_line = n
    if key_line is not None:
        old = lines[key_line]
        lines[key_line] = re.sub(r"^(\s*[^=]*=).*?(\r?\n?)$", lambda m: m.group(1) + value + m.group(2), old)
        return "".join(lines)
    if first is not None:
        if not lines[last].endswith("\n"):
            lines[last] += ending
        lines.insert(last + 1, "{}={}{}".format(key, value, ending))
        return "".join(lines)
    head = text if not text or text.endswith("\n") else text + ending
    return head + ("" if not head else ending) + "[{}]{}{}={}{}".format(section, ending, key, value, ending)


def parse_cfg_values(text):
    """The key = value lines of a settings.cfg, in order."""
    values = {}
    for line in text.splitlines():
        m = re.match(r"^\s*(\w+)\s*=\s*(.*?)\s*$", line)
        if m:
            values[m.group(1)] = m.group(2)
    return values


def set_cfg_values(text, values):
    """settings.cfg with each key set: its line replaced where it is, added before the
    closing brace of the ReDefinition block where it is not."""
    lines = text.splitlines()
    pending = dict(values)
    for i, line in enumerate(lines):
        m = re.match(r"^(\s*)(\w+)\s*=", line)
        if m and m.group(2) in pending:
            lines[i] = "{}{} = {}".format(m.group(1) or "\t", m.group(2), pending.pop(m.group(2)))
    if pending:
        close = max(i for i, line in enumerate(lines) if line.strip() == "}")
        for k, v in pending.items():
            lines.insert(close, "\t{} = {}".format(k, v))
            close += 1
    return "\n".join(lines) + "\n"


def set_ini_values(text, values):
    """ReDefinitionProxy.ini with each key set in its [Proxy] section: the first line of a key
    replaced (GetPrivateProfileString reads the first), a missing key appended to the section."""
    newline = "\r\n" if "\r\n" in text else "\n"
    lines = text.splitlines()
    pending = dict(values)
    section = None
    end_of_proxy = None
    for i, line in enumerate(lines):
        stripped = line.strip()
        if stripped.startswith("[") and stripped.endswith("]"):
            if section == "proxy":
                end_of_proxy = i
            section = stripped[1:-1].strip().lower()
            continue
        if section != "proxy" or stripped.startswith(";"):
            continue
        m = re.match(r"^\s*([A-Za-z0-9_]+)\s*=", line)
        if m and m.group(1) in pending:
            lines[i] = "{}={}".format(m.group(1), pending.pop(m.group(1)))
    if pending:
        if not any(l.strip().lower() == "[proxy]" for l in lines):
            lines.append("[Proxy]")
            end_of_proxy = None
        insert = end_of_proxy if end_of_proxy is not None else len(lines)
        for k, v in pending.items():
            lines.insert(insert, "{}={}".format(k, v))
            insert += 1
    return newline.join(lines) + newline


def parse_ini_sections(text):
    """An ini file's values by section, comments left out: {"DLSSG": {"Preset": "C"}, ...}."""
    sections, current = {}, None
    for line in text.splitlines():
        stripped = line.strip()
        header = re.match(r"^\[(.+)\]$", stripped)
        if header:
            current = sections.setdefault(header.group(1).strip(), {})
        elif current is not None and stripped and not stripped.startswith((";", "#")) and "=" in stripped:
            key, value = stripped.split("=", 1)
            current[key.strip()] = value.strip()
    return sections
