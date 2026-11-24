"""Username validation: format, reserved names, and an inappropriate-word filter.

The word list lives in wordlist.txt next to this file (see its header).
check(name) returns None if the name is fine, otherwise a generic error string.
"""
import os
import re
import unicodedata

FORMAT_RE = re.compile(r"^[A-Za-z0-9_.-]{3,20}$")
NOT_ALLOWED = "That username isn't allowed"

HOMOGLYPHS = str.maketrans({
    "а": "a", "е": "e", "о": "o", "р": "p", "с": "c", "у": "y", "х": "x", "і": "i",
    "ј": "j", "ѕ": "s", "к": "k", "м": "m", "н": "h", "т": "t", "в": "b", "ԁ": "d",
    "α": "a", "ο": "o", "ρ": "p", "ε": "e", "ι": "i", "κ": "k", "ν": "v", "τ": "t",
    "υ": "u", "χ": "x",
})
LEET = {"0": "o", "2": "z", "3": "e", "4": "a", "5": "s", "6": "g", "7": "t",
        "8": "b", "9": "g", "@": "a", "$": "s", "!": "i", "+": "t", "|": "l"}


def _load():
    lists = {"substring": [], "token": [], "allow": [], "reserved": []}
    section = None
    path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "wordlist.txt")
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.split("#", 1)[0].strip().lower()
            if not line:
                continue
            if line.startswith("[") and line.endswith("]"):
                section = line[1:-1]
                continue
            word = re.sub(r"[^a-z0-9]", "", line)
            if section in lists and word:
                lists[section].append(word)
    return lists


WORDS = _load()


def _collapse(s):
    return re.sub(r"(.)\1+", r"\1", s)


def _squeeze(s):
    """Shrink runs of 3+ identical letters to 2 (fuuuck -> fuuck, asss -> ass)."""
    return re.sub(r"(.)\1{2,}", r"\1\1", s)


def _forms(s):
    """(name form, is_fully_collapsed) pairs to check."""
    return ((s, False), (_squeeze(s), False), (_collapse(s), True))


def _fold(s):
    s = unicodedata.normalize("NFKD", s.translate(HOMOGLYPHS))
    return "".join(c for c in s if not unicodedata.combining(c)).lower()


def _leet_variants(s):
    """Map leetspeak; '1' is ambiguous (i or l) and 'v' may stand for 'u'."""
    base = "".join(LEET.get(c, c) for c in s)
    out = {base.replace("1", "i"), base.replace("1", "l")}
    out |= {v.replace("v", "u") for v in out}
    return {re.sub(r"[^a-z]", "", v) for v in out}


def _strip_allowed(s, collapsed):
    for w in WORDS["allow"]:
        w = _collapse(w) if collapsed else w
        s = s.replace(w, "*")
    return s


def is_inappropriate(name):
    folded = _fold(name)
    # Substring check on the whole name with separators removed.
    joined = re.sub(r"[._\-\s]", "", folded)
    for variant in _leet_variants(joined):
        for s, collapsed in _forms(variant):
            s = _strip_allowed(s, collapsed)
            for w in WORDS["substring"]:
                # In the collapsed form only words without doubled letters can
                # match (otherwise "boob" -> "bob" would block Bob).
                if collapsed and _collapse(w) != w:
                    continue
                if w in s:
                    return True
    # Token check: split on separators and camelCase, ignore trailing digits.
    spaced = re.sub(r"([a-z])([A-Z])", r"\1 \2", unicodedata.normalize("NFKD", name))
    tokens = [t for t in re.split(r"[._\-\s]+", _fold(spaced)) if t]
    tokens.append(joined)
    short = set(WORDS["token"])
    short_c = {w for w in short if _collapse(w) == w}
    for tok in tokens:
        for t in (tok, tok.rstrip("0123456789")):
            for v in _leet_variants(t):
                if v in short or _squeeze(v) in short or _collapse(v) in short_c:
                    return True
    return False


def check(name):
    if not isinstance(name, str) or not FORMAT_RE.match(name):
        return "Username must be 3-20 characters: letters, numbers, _ . -"
    if name.lower() in WORDS["reserved"] or re.sub(r"[._-]", "", name.lower()) in WORDS["reserved"]:
        return NOT_ALLOWED
    if is_inappropriate(name):
        return NOT_ALLOWED
    return None
