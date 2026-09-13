import unittest

import numpy as np

from train_board_corners import HEATMAP, SIZE, SyntheticFrames, decode, letterbox


class GeometryTests(unittest.TestCase):
    def test_letterbox_keeps_aspect_and_padding(self):
        image=np.full((1080,1920,3),200,np.uint8)
        output,geometry=letterbox(image)
        self.assertEqual((84,216,384),(geometry[1],geometry[3],geometry[2]))
        self.assertTrue((output[:84]==114).all())
        self.assertTrue((output[84:300]==200).all())

    def test_pixel_center_round_trip_landscape_and_portrait(self):
        yy,xx=np.mgrid[:HEATMAP,:HEATMAP]
        for height,width in [(1080,1920),(1920,1080),(673,1198)]:
            _,geometry=letterbox(np.zeros((height,width,3),np.uint8))
            left,top,rw,rh,_,_=geometry
            point=np.array([width*.31,height*.42])
            hx=((point[0]+.5)*rw/width+left)/2-.5
            hy=((point[1]+.5)*rh/height+top)/2-.5
            maps=np.stack([np.exp(-((xx-hx)**2+(yy-hy)**2)/(2*.7**2))]*4)
            corners,_=decode(maps,geometry)
            np.testing.assert_allclose(corners,np.repeat(point[None],4,axis=0),atol=.12)

    def test_synthetic_empty_and_partial_examples_have_missing_targets(self):
        generator=SyntheticFrames([{"pixels":np.full((480,768,3),150,np.uint8)}],42)
        missing=0
        for _ in range(50):
            image,targets,corners,visible,_=generator.sample()
            self.assertEqual(image.shape,(SIZE,SIZE,3))
            self.assertEqual(targets.shape,(4,HEATMAP,HEATMAP))
            self.assertTrue(np.isfinite(targets).all())
            self.assertTrue((targets[~visible]==0).all())
            missing+=int(not visible.all())
        self.assertGreater(missing,5)


if __name__=="__main__":
    unittest.main()
