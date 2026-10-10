#!/usr/bin/env python3
"""Builds wwwroot/kiosk-words.txt, the word list behind the kiosk keyboard's suggestions and swipe
typing (kiosk-swipe.js).

    python3 core/SentinelCore.Web/scripts/build-kiosk-words.py <path to scowl-YYYY.MM.DD>

SCOWL (http://wordlist.aspell.net, permissive licence, notice copied into the output) has no word
counts, only size levels: 10 is the commonest few thousand words, 20 the next, 35 a small
dictionary. The output is one word per line, commonest first: a hand-ordered list of everyday
words, level 10, words this app gets typed a lot, then level 20 and 35, shorter words first
within a level. A word's line number is its
rank. Words keep their capitals ("I", "Monday"); everything else is lowercase.
"""
import os
import re
import sys

LEVELS = ["10", "20", "35"]
KINDS = ["english-words", "american-words", "english-contractions"]

# Roughly the commonest words of everyday English, in order. Hand-written, so short words that
# would otherwise sit wherever SCOWL's alphabetical level put them rank where people use them.
# Also brings in what SCOWL's small levels leave out: most contractions, days, months.
TOP = """
the to and a I you it of is that in for on was with my this be have not are we me at
but so do just can what if your all he they like get up no out about one go it's know don't
will there now how her she or from his an time I'm see good when been would more then them
here some think back want had has by our got going did can't want day new him who also make
well really need over love today home take still right too people last come after night yes ok
much should way off work tomorrow could two first let call these any said tell did very only
again thanks thank where why why'd because before am never next even than something year down
check little many morning other same long into look week those say great put help please sure
find were while every around show better best feel give doing let's that's I'll I've what's
there's he's she's we're they're you're isn't wasn't aren't didn't doesn't won't wouldn't
couldn't shouldn't haven't hasn't hadn't weren't I'd you'll we'll they'll you've we've
they've you'd we'd she'll he'll it'll that'll who's where's how's
hello hi hey okay yeah reminder
Monday Tuesday Wednesday Thursday Friday Saturday Sunday
January February March April May June July August September October November December
""".split()

# Words this app gets typed a lot (reminders, notes, searches): ranked just after SCOWL's
# commonest level, ahead of the rest.
HOME = """
remind reminder reminders note notes event events camera cameras front backyard door doors yard
garage driveway porch dinner lunch breakfast school pick buy milk eggs bread store groceries kids
mom dad doctor appointment dentist practice trash recycling laundry dishes vacuum mow lawn
""".split()



def read(path):
    with open(path, encoding="latin-1") as f:
        return [line.strip() for line in f if line.strip()]


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    final = os.path.join(sys.argv[1], "final")
    with open(os.path.join(sys.argv[1], "Copyright"), encoding="latin-1") as f:
        notice = f.read().split("\n\n")[0:3]

    seen = set()
    out = []

    def add(word):
        key = word.lower()
        if key in seen:
            return
        seen.add(key)
        out.append(word)

    for word in TOP:
        add(word)
    for level in LEVELS:
        words = []
        for kind in KINDS:
            path = os.path.join(final, f"{kind}.{level}")
            if os.path.exists(path):
                words += read(path)
        # Letters and apostrophes only, no possessives ("dog's"), no one-letter words but a and I.
        words = [w for w in words
                 if re.fullmatch(r"[a-z]+(?:'[a-z]+)?|I'[a-z]+", w)
                 and not w.endswith("'s")
                 and (len(w) > 1 or w in ("a",))]
        words.sort(key=lambda w: (len(w), w))
        for word in words:
            add(word)
        if level == LEVELS[0]:
            for word in HOME:
                add(word)

    dest = os.path.join(os.path.dirname(__file__), "..", "wwwroot", "kiosk-words.txt")
    with open(dest, "w", encoding="utf-8") as f:
        f.write("# Word list for the kiosk keyboard, commonest first. Built by\n")
        f.write("# core/SentinelCore.Web/scripts/build-kiosk-words.py from SCOWL "
                f"({os.path.basename(os.path.normpath(sys.argv[1]))}):\n#\n")
        for para in notice:
            for line in para.splitlines():
                f.write(("# " + line.strip()).rstrip() + "\n")
            f.write("#\n")
        f.write("\n".join(out) + "\n")
    print(f"{len(out)} words -> {os.path.normpath(dest)}")


if __name__ == "__main__":
    main()
