from pathlib import Path
from PIL import Image, ImageDraw
root=Path(__file__).resolve().parents[1]
assets=root/'src/Filee.App/Assets/Icons'
im=Image.new('RGBA',(1024,1024),(0,0,0,0));d=ImageDraw.Draw(im)
d.rounded_rectangle((56,56,968,968),radius=218,fill='#24483D')
d.rounded_rectangle((224,174,758,850),radius=58,fill='#F5F7ED')
d.polygon([(608,174),(758,324),(608,324)],fill='#AAC9B7')
d.polygon([(608,174),(758,174),(758,324)],fill='#24483D')
d.rounded_rectangle((304,374,540,406),radius=16,fill='#9CAD9F')
d.rounded_rectangle((304,443,651,475),radius=16,fill='#9CAD9F')
d.rounded_rectangle((298,541,684,731),radius=48,fill='#397F61')
d.line([(352,603),(610,603)],fill='white',width=20)
d.polygon([(586,565),(637,603),(586,641)],fill='white')
d.line([(625,674),(370,674)],fill='white',width=20)
d.polygon([(394,638),(344,674),(394,710)],fill='white')
im.save(assets/'convertdesk.png')
im.save(assets/'convertdesk.ico',sizes=[(16,16),(32,32),(48,48),(64,64),(128,128),(256,256)])
iconset=root/'installer/macos/convertdesk.iconset';iconset.mkdir(exist_ok=True)
for px in [16,32,128,256,512]:
 im.resize((px,px),Image.Resampling.LANCZOS).save(iconset/f'icon_{px}x{px}.png')
 im.resize((px*2,px*2),Image.Resampling.LANCZOS).save(iconset/f'icon_{px}x{px}@2x.png')
