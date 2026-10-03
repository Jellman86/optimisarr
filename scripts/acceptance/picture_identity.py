"""Read the binary luminance markers in the owned, numbered DTS fixture."""
from .core import require


def parse_picture_ids(raw):
    width, height, cell = 320, 48, 32
    size = width * height
    require(raw and len(raw) % size == 0 and len(raw) <= 1000 * size,
            "Missing, partial or unbounded numbered pictures")
    identities = []
    for start in range(0, len(raw), size):
        number = 0
        for bit in range(10):
            value = sum(raw[start + y * width + bit * cell + x]
                        for y in range(8, 40) for x in range(8, 24)) / 512
            require(value < 80 or value > 175, "Ambiguous picture identity marker")
            if value > 175:
                number |= 1 << bit
        identities.append(number)
    return identities


def validate_picture_ids(reference, candidate):
    require(reference and reference == list(range(len(reference))),
            "Numbered source has lost, repeated or reordered pictures")
    require(candidate == reference, "Candidate has lost, repeated or reordered pictures")
    return len(reference)
