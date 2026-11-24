#!/usr/bin/env python3
"""Makes vision_words.txt: for each of MobileNetV2's 1000 ImageNet classes, the words that find it in search.

Line i (0-based) is the class's own name(s) then '|' then broader words, comma separated, e.g.
    golden retriever|dog,animal,pet
The apps search both parts; the broader words are also offered as suggestions ("Dogs", "Beach", "Food").
Usage: make_vision_words.py labels.txt out.txt   (labels.txt: one ImageNet class name per line)
"""
import sys

R = lambda a, b: list(range(a, b + 1))

GROUPS = {
    "fish": R(0, 6) + R(389, 397),
    "shark": R(2, 4),
    "bird": R(7, 24) + R(80, 100) + R(127, 146),
    "chicken": [7, 8],
    "duck": [97, 98, 99],
    "parrot": R(87, 90),
    "owl": [24],
    "penguin": [145],
    "frog": R(30, 32),
    "turtle": R(33, 37),
    "lizard": R(38, 48),
    "crocodile": [49, 50],
    "dinosaur": [51],
    "snake": R(52, 68),
    "spider": R(72, 77),
    "elephant": [101, 385, 386],
    "kangaroo": [104],
    "koala": [105],
    "jellyfish": [107],
    "snail": [113],
    "crab": R(118, 121) + [125],
    "lobster": R(122, 124),
    "whale": [147, 148],
    "dolphin": [148],
    "seal": [150],
    "dog": R(151, 268),
    "puppy": R(151, 268),
    "wolf": R(269, 272),
    "fox": R(277, 280),
    "cat": R(281, 285),
    "kitten": R(281, 285),
    "big cat": R(286, 293),
    "lion": [291],
    "tiger": [292],
    "leopard": [288, 289, 290],
    "bear": R(294, 297),
    "insect": R(300, 326),
    "bug": R(300, 320),
    "beetle": R(300, 307),
    "bee": [309],
    "ant": [310],
    "butterfly": R(321, 326),
    "starfish": [327],
    "rabbit": R(330, 332),
    "bunny": R(330, 332),
    "hamster": [333, 338],
    "squirrel": [335],
    "horse": [339, 603],
    "zebra": [340],
    "pig": R(341, 343),
    "cow": [345, 346, 347],
    "sheep": [348, 349],
    "goat": [350],
    "deer": R(351, 353),
    "camel": [354],
    "llama": [355],
    "otter": [360],
    "monkey": R(365, 384),
    "ape": [365, 366, 367],
    "panda": [387, 388],
    "car": [407, 436, 468, 511, 609, 627, 656, 661, 717, 751, 817, 581, 479, 573, 705],
    "truck": [555, 569, 675, 717, 864, 867],
    "bus": [779, 874, 654],
    "van": [656, 675, 734],
    "taxi": [468],
    "bicycle": [444, 671, 870, 880],
    "bike": [444, 671, 670, 665],
    "motorcycle": [670, 665],
    "train": [466, 547, 565, 705, 820, 829],
    "tram": [829, 874],
    "airplane": [404, 895, 908],
    "plane": [404, 895, 908],
    "aircraft": [404, 405, 895, 908],
    "boat": [472, 484, 510, 554, 576, 625, 628, 724, 780, 814, 833, 871, 914, 693],
    "ship": [510, 628, 724, 780, 833],
    "sailing": [484, 780, 871, 914],
    "tractor": [866],
    "food": R(924, 965),
    "fruit": R(948, 957),
    "vegetable": R(936, 946) + [987],
    "dessert": [927, 928, 929, 960],
    "ice cream": [928, 929],
    "bread": [930, 931, 932],
    "burger": [933],
    "pizza": [963],
    "pasta": [959],
    "drink": [966, 967, 968, 969, 440, 441, 907, 504, 898],
    "coffee": [967, 504],
    "wine": [966, 907],
    "beer": [440, 441],
    "mountain": [970, 980, 979, 672],
    "beach": [978, 977, 976],
    "sea": [978, 973, 976, 977],
    "ocean": [978, 973, 976],
    "lake": [975],
    "river": [975, 979],
    "landscape": [970] + R(972, 980),
    "nature": [970] + R(972, 980) + R(984, 997),
    "volcano": [980],
    "cliff": [972, 976],
    "valley": [979],
    "underwater": [973, 983],
    "diving": [983],
    "flower": [985, 986, 984],
    "plant": [984, 985, 986, 738],
    "mushroom": [947] + R(991, 997),
    "tree": [988, 990],
    "snow": [795, 802, 803, 537],
    "skiing": [795],
    "ball": [429, 430, 522, 574, 722, 768, 805, 852, 890],
    "sports": [429, 430, 522, 574, 722, 768, 805, 852, 890, 981, 795, 801],
    "football": [768, 805],
    "soccer": [805],
    "tennis": [852],
    "basketball": [430],
    "baseball": [429, 981],
    "golf": [574, 575],
    "bridge": [821, 839, 888],
    "building": [425, 449, 483, 497, 498, 580, 663, 668, 698, 727, 832, 437, 762, 500, 538],
    "church": [497, 663],
    "castle": [483, 698],
    "lighthouse": [437],
    "tower": [437, 832],
    "barn": [425],
    "house": [425, 449, 500, 858],
    "restaurant": [762],
    "shop": [865, 582, 509, 454, 860],
    "chair": [423, 559, 765, 857],
    "sofa": [831],
    "table": [532, 526],
    "bed": [564, 520, 431],
    "baby": [431, 520, 529],
    "computer": [620, 527, 664, 782, 590, 508, 673],
    "laptop": [620],
    "phone": [487, 528, 707],
    "camera": [759, 732],
    "tv": [851, 782],
    "screen": [664, 782, 851],
    "keyboard": [508, 579, 881],
    "piano": [579, 881],
    "guitar": [402, 546],
    "music": [402, 546, 579, 881, 486, 513, 558, 566, 593, 594, 641, 683, 684, 776, 822, 875, 889],
    "instrument": [402, 546, 579, 881, 486, 513, 558, 566, 593, 594, 641, 683, 684, 776, 822, 875, 889],
    "toy": [850, 573, 417],
    "balloon": [417],
    "teddy": [850],
    "umbrella": [879],
    "clock": [409, 892, 530],
    "watch": [826, 531],
    "glasses": [836, 837],
    "sunglasses": [836, 837],
    "hat": [515, 808, 793, 452, 560],
    "clothes": [399, 400, 411, 474, 501, 515, 578, 601, 608, 610, 617, 655, 689, 697, 735, 775, 834, 841, 869],
    "dress": [578, 655],
    "shoe": [514, 630, 770, 774],
    "bag": [414, 728, 748, 893],
    "book": [921, 454, 624],
    "car wheel": [479],
    "traffic light": [920],
    "street": [919, 920, 704, 829],
    "christmas": [496],
    "pumpkin": [607],
    "candle": [470],
    "fire": [470, 555],
    "fountain": [562],
    "swimming": [445, 638, 639, 842, 983],
    "swimsuit": [445, 638, 639, 842],
    "tent": [672],
    "camping": [672],
    "wedding": [982, 578],
    "tools": [587, 784, 596, 740, 491, 731],
    "garden": [621, 716, 703, 738],
    "halloween": [607],
    "stage": [819],
    "kitchen": [567, 813, 909, 659, 760, 766, 651, 469, 550],
}

ANIMALS = set(R(0, 397))
PETS = set(R(151, 268) + R(281, 285) + R(330, 333) + [338, 1, 87, 88, 89, 90])
VEHICLES = set(sum((GROUPS[k] for k in ["car", "truck", "bus", "bicycle", "motorcycle", "train", "airplane", "boat", "tractor"]), []))


def main():
    labels = open(sys.argv[1], encoding="utf-8").read().splitlines()
    assert len(labels) == 1000, len(labels)
    out = []
    for i, name in enumerate(labels):
        words = []
        for g, ids in GROUPS.items():
            if i in ids:
                words.append(g)
        if i in ANIMALS:
            words.append("animal")
        if i in PETS:
            words.append("pet")
        if i in VEHICLES:
            words.append("vehicle")
        seen = []
        for w in words:
            if w not in seen and w != name.lower():
                seen.append(w)
        out.append(f"{name.lower()}|{','.join(seen)}")
    with open(sys.argv[2], "w", encoding="utf-8", newline="\n") as fh:
        fh.write("\n".join(out) + "\n")


if __name__ == "__main__":
    main()
