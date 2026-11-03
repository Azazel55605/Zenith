"""Independent scalar frame oracle and public rendered Terminal observation."""
import io
import time
from PIL import Image

def expected():
    tiles=[]
    for seed in range(4):
        tile=[]
        for p in range(4096):
            value=0xff000000|((seed*33+p*17)&0xffffff)
            for r in range(32):
                tint=0x203040+((r+seed)&15)*0x010101
                channels=[(((value>>s)&255)*191+((tint>>s)&255)*64+127)//255 for s in (16,8,0)]
                value=0xff000000|(channels[0]<<16)|(channels[1]<<8)|channels[2]
            tile.append(bytes(((value>>16)&255,(value>>8)&255,value&255)))
        tiles.append(tile)
    rows=[]
    for y in range(128):
        rows.append(b''.join(tiles[(y//64)*2+x//64][(y%64)*64+x%64] for x in range(128)))
    return rows

def locate(ppm,rows):
    image=Image.open(io.BytesIO(ppm)).convert('RGB');w,h=image.size;data=image.tobytes()
    start=0
    while True:
        offset=data.find(rows[0],start)
        if offset<0:return None
        start=offset+1
        if offset%3:continue
        x,y=(offset//3)%w,(offset//3)//w
        if x+128<=w and y+128<=h and all(data[((y+r)*w+x)*3:((y+r)*w+x+128)*3]==row for r,row in enumerate(rows)):
            return x,y

def live_prompt(ppm,box):
    # A fresh live recovery Terminal uses a neutral "~$", rather than the
    # installed styled cwd expected by the shared settings fixture. Require a
    # real focused caret, green user/host on that same row, then neutral prompt
    # glyphs immediately before it. Hidden prompts have no green user/host.
    image=Image.open(io.BytesIO(ppm)).convert('RGB')
    left,top,_,bottom=box;height=bottom-top;start=max(0,left-12*height)
    green=[];neutral=[]
    for y in range(top,bottom):
        for x in range(start,left):
            r,g,b=image.getpixel((x,y))
            if g-r>20 and g-b>10:green.append(x)
            if min(r,g,b)>130 and max(r,g,b)-min(r,g,b)<45:neutral.append(x)
    return (len(green)>=12 and max(green)-min(green)>=2*height and len(neutral)>=8
            and max(green)<max(neutral) and max(neutral)>left-2*height)

def prompt(observer,terminal,timeout=20):
    previous=observer.capture(observer.snapshot);deadline=time.monotonic()+timeout
    while time.monotonic()<deadline:
        time.sleep(.1);current=observer.capture(observer.snapshot)
        box=terminal.caret_box(previous,current)
        if box and (terminal.public_prompt(current,box) or live_prompt(current,box)):return
        previous=current
    raise RuntimeError('rendered public Terminal prompt unavailable')
