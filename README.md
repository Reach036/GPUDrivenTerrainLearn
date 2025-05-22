# GPU Driven Terrain 入门
源项目: https://github.com/wlgys8/GPUDrivenTerrainLearn
# 优化部分:
1.修复镜头快速旋转时，画面边缘剔除不正确的问题

2.为地块shader添加阴影接收计算

3.尝试实现HizDepth链，减少dispatch次数，参考自: https://zhuanlan.zhihu.com/p/335325149

4.使用更好的视锥剔除算法，参考自: https://zhuanlan.zhihu.com/p/648843014

5.使用优化的hiz剔除，对于投影后aabb长宽比大于2的Bounds，拆分为两个进行更精细的剔除，参考自: https://zhuanlan.zhihu.com/p/540479878
![11](https://github.com/user-attachments/assets/4a1dcc98-984e-4d2b-95e6-42b40c37f221)

版本: Unity2022.3
