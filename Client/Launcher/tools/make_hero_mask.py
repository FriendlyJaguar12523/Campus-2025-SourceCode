"""Builds HeroMask.png: where Hero.jpg is backdrop or logo letters (white) - the parts the launcher
repaints in the player's gradient (App::PaintHero). Run after changing Hero.jpg:

  python tools/make_hero_mask.py

The characters' skin and red hair are as saturated as the purple->orange backdrop, so colour alone
can't separate them. Instead:
  1. fit a smooth model of the backdrop (least squares, cubic in x/y) to clearly-backdrop pixels,
  2. a pixel is backdrop only if it matches that model in chroma AND hue, at any brightness (the
     backdrop in shadow round the logo is still backdrop; red hair is a few degrees off in hue,
     skin is off in chroma),
  3. keep only the big connected backdrop region(s) - an isolated patch inside a character (a hat
     band, a highlight) is never backdrop - and fill small specks in the backdrop,
  4. add the logo's letters: the saturated pixels its white outline encloses.
"""
import os
import numpy as np
from PIL import Image, ImageFilter
from scipy import ndimage

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
im = np.asarray(Image.open(os.path.join(ROOT, 'Hero.jpg')).convert('RGB')).astype(np.float32) / 255
H, W, _ = im.shape
ys, xs = np.mgrid[0:H, 0:W]
X, Y = xs / W, ys / H


def feats(x, y):
    return np.stack([np.ones_like(x), x, y, x * x, x * y, y * y, x ** 3, y ** 3, x * x * y, x * y * y], -1)


def hue(rgb):
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    mx, mn = rgb.max(-1), rgb.min(-1)
    d = np.maximum(mx - mn, 1e-6)
    h = np.where(mx == r, ((g - b) / d) % 6, np.where(mx == g, (b - r) / d + 2, (r - g) / d + 4)) * 60
    return h


F = feats(X, Y).reshape(-1, 10)
P = im.reshape(-1, 3)
mx, mn = P.max(1), P.min(1)
s = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1e-6), 0)
sel = (s > 0.55) & (mx > 0.5) & ((Y < 0.33).reshape(-1) | ((X > 0.33) & (X < 0.70) & (Y > 0.66)).reshape(-1))
for _ in range(5):
    coef, *_ = np.linalg.lstsq(F[sel], P[sel], rcond=None)
    err = np.abs(F @ coef - P).max(1)
    sel = (err < 0.07) & (s > 0.45)
model = np.clip((F @ coef).reshape(H, W, 3), 0, 1)

# compare colour apart from brightness: the backdrop in shadow (round the logo's outline, behind
# the characters) is still backdrop. Hue catches the red hair, chroma catches skin.
vim, vmod = im.max(2), np.maximum(model.max(2), 1e-6)
chroma_err = np.abs(im / np.maximum(vim, 1e-6)[..., None] - model / vmod[..., None]).max(2)
ratio = vim / vmod
dh = np.abs(hue(model) - hue(im))
dh = np.minimum(dh, 360 - dh)
bg = (1 - np.clip((chroma_err - 0.07) / 0.08, 0, 1)) * (1 - np.clip((dh - 4) / 5, 0, 1))      * np.clip((ratio - 0.30) / 0.15, 0, 1) * (1 - np.clip((ratio - 1.12) / 0.1, 0, 1))

# connected regions: keep the big backdrop, fill specks
solid = bg > 0.5
lab, n = ndimage.label(solid)
sizes = ndimage.sum(solid, lab, range(1, n + 1))
keep = np.isin(lab, 1 + np.nonzero(sizes > 0.02 * H * W)[0])
holes, hn = ndimage.label(~keep)
hsizes = ndimage.sum(~keep, holes, range(1, hn + 1))
keep |= np.isin(holes, 1 + np.nonzero(hsizes < 400)[0])
bg = np.where(keep, np.maximum(bg, 0.0), 0.0)
bg = np.where(keep, np.clip(bg * 1.5, 0, 1), 0.0)

# the logo's letters: what its white outline encloses (filling the outline's holes), so nothing that
# merely touches the logo from outside - the girl's red hair does, right of the "S" - counts
smap, vmap = s.reshape(H, W), mx.reshape(H, W)
box = (X > 0.24) & (X < 0.78) & (Y > 0.33) & (Y < 0.67)
white = box & (smap < 0.15) & (vmap > 0.82)
white = ndimage.binary_closing(white, iterations=2)
inside = ndimage.binary_fill_holes(white) & ~white
logo = np.where(inside, np.clip((smap - 0.35) / 0.25, 0, 1), 0)

m = np.maximum(bg, logo)
mask = Image.fromarray((m * 255).astype(np.uint8), 'L')
mask = mask.filter(ImageFilter.MaxFilter(3)).filter(ImageFilter.GaussianBlur(1.0))   # cover the backdrop's fringe
mask.save(os.path.join(ROOT, 'HeroMask.png'), optimize=True)
print('HeroMask.png', mask.size)
