"""README 구성도 생성. 원본 벡터(SVG)와 한글 폰트를 적용한 PNG를 함께 출력한다."""
from pathlib import Path
from html import escape
import math
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[1]
W,H=2400,1510
img=Image.new('RGB',(W,H),'#ffffff'); draw=ImageDraw.Draw(img)
svg=[f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}">', '<rect width="100%" height="100%" fill="white"/>']
font_dir=Path('C:/Windows/Fonts')
def font(size,bold=False):return ImageFont.truetype(str(font_dir/('malgunbd.ttf' if bold else 'malgun.ttf')),size)
def rect(box,fill,stroke='#d5dfeb',radius=18,width=2):
 x1,y1,x2,y2=box
 draw.rounded_rectangle(box,radius=radius,fill=fill,outline=stroke,width=width)
 svg.append(f'<rect x="{x1}" y="{y1}" width="{x2-x1}" height="{y2-y1}" rx="{radius}" fill="{fill}" stroke="{stroke}" stroke-width="{width}"/>')
def text(x,y,value,size=27,color='#283e56',bold=False,center=False):
 f=font(size,bold)
 if center:x-=draw.textlength(value,font=f)/2
 draw.text((x,y),value,font=f,fill=color)
 svg.append(f'<text x="{x}" y="{y+size}" fill="{color}" font-family="Malgun Gothic, sans-serif" font-size="{size}" font-weight="{700 if bold else 400}">{escape(value)}</text>')
def arrow(x1,y1,x2,y2,color='#4773ad',both=False):
 draw.line((x1,y1,x2,y2),fill=color,width=4)
 svg.append(f'<path d="M {x1} {y1} L {x2} {y2}" stroke="{color}" stroke-width="4" fill="none"/>')
 def tip(x,y,a):
  pts=[(x,y),(x-14*math.cos(a-.48),y-14*math.sin(a-.48)),(x-14*math.cos(a+.48),y-14*math.sin(a+.48))]
  draw.polygon(pts,fill=color)
  svg.append('<polygon points="'+' '.join(f'{px},{py}' for px,py in pts)+f'" fill="{color}"/>')
 a=math.atan2(y2-y1,x2-x1);tip(x2,y2,a)
 if both:tip(x1,y1,a+math.pi)

def line(points,color='#4773ad',width=4):
 draw.line(points,fill=color,width=width,joint='curve')
 svg.append('<polyline points="'+' '.join(f'{x},{y}' for x,y in points)+f'" stroke="{color}" stroke-width="{width}" fill="none" stroke-linecap="round" stroke-linejoin="round"/>')
def ellipse(box,fill='white',stroke='#4773ad',width=4):
 x1,y1,x2,y2=box
 draw.ellipse(box,fill=fill,outline=stroke,width=width)
 svg.append(f'<ellipse cx="{(x1+x2)/2}" cy="{(y1+y2)/2}" rx="{(x2-x1)/2}" ry="{(y2-y1)/2}" fill="{fill}" stroke="{stroke}" stroke-width="{width}"/>')
def icon(cx,cy,kind,color):
 if kind=='camera':
  rect((cx-60,cy-36,cx+60,cy+34),'white',color,12,4)
  ellipse((cx-23,cy-25,cx+23,cy+21),'#edf4fd',color,4)
  line([(cx-23,cy+36),(cx-23,cy+48),(cx+23,cy+48)],color,4)
 elif kind=='app':
  rect((cx-66,cy-43,cx+66,cy+38),'white',color,10,4)
  line([(cx-65,cy-19),(cx+65,cy-19)],color,3)
  for dx in [-48,-33,-18]:ellipse((cx+dx-3,cy-33,cx+dx+3,cy-27),color,color,1)
  ellipse((cx-14,cy-7,cx+14,cy+18),'#edf4fd',color,3)
  line([(cx-30,cy+29),(cx-20,cy+19),(cx+20,cy+19),(cx+30,cy+29)],color,3)
 elif kind=='server':
  for dy in [-43,-10,23]:
   rect((cx-60,cy+dy,cx+60,cy+dy+26),'white',color,6,3)
   ellipse((cx-46,cy+dy+9,cx-38,cy+dy+17),color,color,1)
   line([(cx-20,cy+dy+13),(cx+40,cy+dy+13)],color,3)
 elif kind=='ai':
  rect((cx-38,cy-38,cx+38,cy+38),'#edf4fd',color,10,4)
  for t in [-22,0,22]:
   for side in [-1,1]:
    line([(cx+side*38,cy+t),(cx+side*53,cy+t)],color,3)
    line([(cx+t,cy+side*38),(cx+t,cy+side*53)],color,3)
  text(cx,cy-22,'AI',30,color,True,True)
 elif kind=='db':
  rect((cx-54,cy-35,cx+54,cy+31),'white',color,1,3)
  ellipse((cx-54,cy+12,cx+54,cy+49),'white',color,3)
  rect((cx-51,cy-30,cx+51,cy+28),'white','white',0,1)
  ellipse((cx-54,cy-49,cx+54,cy-12),'#edf4fd',color,3)
  line([(cx-54,cy-29),(cx-54,cy+29)],color,3)
  line([(cx+54,cy-29),(cx+54,cy+29)],color,3)
 elif kind=='files':
  rect((cx-45,cy-48,cx+50,cy+35),'#edf4fd',color,6,3)
  rect((cx-60,cy-31,cx+35,cy+52),'white',color,6,3)
  for dy in [-9,7,23]:line([(cx-42,cy+dy),(cx+17,cy+dy)],color,3)
 elif kind=='monitor':
  rect((cx-66,cy-43,cx+66,cy+31),'white',color,8,4)
  line([(cx,cy+31),(cx,cy+49),(cx-30,cy+49),(cx+30,cy+49)],color,4)
  line([(cx-43,cy+10),(cx-22,cy-8),(cx-4,cy+4),(cx+25,cy-23),(cx+43,cy-6)],color,4)
def card(cx,cy,title,kind,detail,color='#3668a8',width=290):
 rect((cx-width/2,cy-105,cx+width/2,cy+112),'white','#d8e3ef',18,2)
 icon(cx,cy-27,kind,color)
 text(cx,cy+36,title,29,color,True,True)
 text(cx,cy+78,detail,22,'#61758a',False,True)
def label(cx,y,title,color='#3668a8',size=25):
 tw=draw.textlength(title,font=font(size,True))
 rect((cx-tw/2-12,y-3,cx+tw/2+12,y+size+9),'white','white',5,1)
 text(cx,y,title,size,color,True,True)
def route(points,color='#3668a8'):
 line(points,color)
 arrow(*points[-2],*points[-1],color)

BLUE='#3668a8'; GREEN='#27806a'; AMBER='#b57a2c'
text(65,34,'얼굴 등록에서 출입 판정까지',47,'#172d49',True)
text(67,102,'전체 시스템 아키텍처 · 카메라 / 앱 / AI / DB의 실제 사용 흐름',26,'#64778b')
rect((55,170,1165,1390),'#f4f7fc','#c4d4e9',25)
rect((1410,170,2345,1390),'#f3f9f6','#bed9cf',25)
text(92,195,'관리 서버 PC',34,BLUE,True)
text(1448,195,'카메라 클라이언트 PC',34,GREEN,True)
text(95,245,'등록 · 권한 판정 · 기록 조회',23,'#6b7f98')
text(1448,245,'카메라 입력 · 직원 식별 · 영상 전송',23,'#6b7f98')
# 등록은 앱에서 AI에 프레임을 전달하고 결과를 받아 서버에 저장을 요청한다.
card(220,415,'등록 카메라','camera','직원 얼굴 촬영',BLUE,260)
card(560,415,'얼굴 등록 앱','app','직원 선택 · 수집 안내',BLUE,290)
card(950,415,'등록 AI','ai','검출 · 특징 · 샘플 선택',BLUE,310)
arrow(350,415,415,415,BLUE)
arrow(705,415,795,415,BLUE,True)
label(750,334,'분석',BLUE,22)
route([(560,527),(560,595),(950,595),(950,700)],BLUE)
label(755,573,'① 완성된 얼굴 특징 저장 요청',BLUE,25)
# 판정과 저장소는 분리한다. 분석 후보의 최종 허가는 서버에서만 결정한다.
card(950,810,'관리 서버','server','최종 판정 · 관제',BLUE,310)
card(310,810,'PostgreSQL','db','직원 · 얼굴 · 권한 · 이력',BLUE,370)
arrow(495,810,795,810,BLUE,True)
label(643,755,'② 등록 저장 / 권한 조회',BLUE,24)
text(645,835,'출입 결과·영상 정보 기록',21,'#6b7f98',False,True)
card(1630,415,'인증 카메라','camera','출입자 촬영',GREEN,310)
card(1630,810,'인증 앱','app','입력 · 후보 전송 · 결과 표시',GREEN,350)
arrow(1630,527,1630,700,GREEN)
label(1630,593,'③ 실시간 프레임',GREEN,25)
card(2130,810,'인증 AI','ai','특징 추출 · 직원 비교',GREEN,330)
arrow(1805,810,1965,810,GREEN,True)
label(1885,749,'분석',GREEN,22)
text(1885,845,'후보',21,GREEN,False,True)
# 두 PC 사이의 통신은 메시지 목적별로 구분한다.
arrow(1105,738,1455,738,BLUE)
label(1280,696,'직원 얼굴 기준 동기화',BLUE,23)
arrow(1455,810,1105,810,GREEN)
label(1280,772,'④ 식별 후보 전송',GREEN,24)
arrow(1105,882,1455,882,BLUE)
label(1280,844,'⑤ 허가 / 거부 응답',BLUE,24)
text(1280,1023,'TLS · TCP/IP',21,'#78889b',False,True)
# 실시간 관제·녹화는 얼굴 인식과 별도 흐름이며 원본은 클라이언트에 보관한다.
card(950,1195,'기록 · 영상 확인','monitor','출입 이력 · 타임라인 · 클립',AMBER,340)
card(2000,1195,'로컬 영상 저장','files','MP4 원본 · 클립 / JSON',AMBER,470)
arrow(950,922,950,1085,AMBER)
label(950,981,'⑥ 기록 조회',AMBER,24)
route([(1630,922),(1630,970),(1120,970),(1120,916),(1105,916)],AMBER)
label(1335,938,'실시간 JPEG 영상',AMBER,23)
route([(1630,922),(1630,1000),(2000,1000),(2000,1085)],AMBER)
label(1845,979,'카메라 구간 녹화',AMBER,24)
arrow(1765,1195,1120,1195,AMBER,True)
label(1443,1142,'⑦ 시각 선택 → 영상 요청 / 제공',AMBER,24)
text(1443,1231,'녹화 영상은 요청한 구간을 제공',22,AMBER,False,True)
text(95,1327,'서버에서 얼굴 등록 → 클라이언트에 기준 동기화 → 식별 후보를 서버가 최종 판정',25,BLUE)
text(67,1430,'파랑: 등록·서버 처리     초록: 실시간 식별     주황: 영상·기록',24,'#566d84')
text(67,1470,'논리적 배치도 · 동일 PC 실행 가능 · 실제 두 PC 통합 완료를 뜻하지 않음',22,'#78889b')
svg.append('</svg>')
img.save(ROOT/'images/system-architecture.png')
(ROOT/'images/system-architecture.svg').write_text('\n'.join(svg),encoding='utf-8')
print('Created architecture PNG + SVG:',W,H)
